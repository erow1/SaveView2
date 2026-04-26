using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using SafeView.Application.Abstractions.Diagnostics;
using SafeView.Application.Configuration;
using SafeView.Domain.SystemEvents;

namespace SafeView.Infrastructure.Diagnostics;

/// <summary>
/// Serilog sink który zapisuje zdarzenia (Warning+, konfigurowalne) do MongoDB
/// przez <see cref="ISystemEventLogger"/>. Dzięki temu każde <c>_log.LogError(ex, ...)</c>
/// w dowolnym miejscu kodu automatycznie ląduje w UI /admin/system-events — bez
/// ręcznego instrumentowania catch blocks.
///
/// UWAGA: <see cref="ISystemEventLogger"/> jest resolvowany <b>lazy</b> (przy pierwszym
/// zdarzeniu), nie w konstruktorze. Inaczej powstaje circular dependency podczas
/// budowy hosta: Serilog → resolve ISystemEventLogger → ctor wymaga ILogger → LoggerFactory
/// → Serilog który jeszcze się buduje → deadlock.
///
/// Zapis jest asynchroniczny (fire-and-forget z wewnętrzną kolejką), żeby log nie
/// blokował wątku wywołującego. Przy shutdownie <see cref="Dispose"/> flushuje kolejkę.
/// </summary>
public sealed class SystemEventSink : ILogEventSink, IDisposable
{
    private readonly IServiceProvider _sp;
    private ISystemEventLogger? _logger;
    private readonly Lock _loggerLock = new();
    private readonly IOptionsMonitor<DiagnosticsOptions> _opts;
    private readonly ConcurrentQueue<LogEvent> _queue = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _worker;
    private readonly SemaphoreSlim _signal = new(0);

    // Prevent infinite loop — jeśli sam sink/logger spróbuje zalogować,
    // ignorujemy (AsyncLocal flaga ustawiana w Emit).
    private static readonly AsyncLocal<bool> _inEmit = new();

    // Maks rozmiar kolejki — gdy przepełniona, pomijamy nowe eventy
    // (lepiej stracić ostatnie niż wywalić aplikację OOM-em).
    private const int MaxQueueSize = 10_000;

    public SystemEventSink(IServiceProvider sp, IOptionsMonitor<DiagnosticsOptions> opts)
    {
        _sp = sp;
        _opts = opts;
        _worker = Task.Run(ProcessQueueAsync);
    }

    private ISystemEventLogger? TryGetLogger()
    {
        if (_logger is not null) return _logger;
        lock (_loggerLock)
        {
            if (_logger is not null) return _logger;
            try
            {
                _logger = _sp.GetService<ISystemEventLogger>();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SystemEventSink] Failed to resolve ISystemEventLogger: {ex.Message}");
            }
            return _logger;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (_inEmit.Value) return; // reentrancy guard
        if (_stopCts.IsCancellationRequested) return;

        var opts = _opts.CurrentValue;
        if (!opts.Enabled) return;

        var severity = MapLevel(logEvent.Level);
        if (severity < opts.MinimumLevel) return;

        if (_queue.Count >= MaxQueueSize) return;

        _queue.Enqueue(logEvent);
        try { _signal.Release(); } catch { /* disposed */ }
    }

    private async Task ProcessQueueAsync()
    {
        while (!_stopCts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            while (_queue.TryDequeue(out var logEvent))
            {
                var logger = TryGetLogger();
                if (logger is null) continue; // DI nie gotowe lub błąd resolve — drop event
                try
                {
                    _inEmit.Value = true;
                    var evt = ToSystemEvent(logEvent, _opts.CurrentValue);
                    await logger.LogAsync(evt, _stopCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SystemEventSink] Failed to persist: {ex.Message}");
                }
                finally
                {
                    _inEmit.Value = false;
                }
            }
        }
    }

    private static SystemEvent ToSystemEvent(LogEvent logEvent, DiagnosticsOptions opts)
    {
        // Source: SourceContext property — ustawiany przez Microsoft.Extensions.Logging
        // jako pełna nazwa klasy (np. "SafeView.Cameras.MediaMtxManager").
        // Bierzemy ostatni segment po '.' dla czytelności UI.
        var source = "Unknown";
        if (logEvent.Properties.TryGetValue("SourceContext", out var ctxVal)
            && ctxVal is ScalarValue { Value: string s } && !string.IsNullOrEmpty(s))
        {
            var dot = s.LastIndexOf('.');
            source = dot >= 0 && dot < s.Length - 1 ? s[(dot + 1)..] : s;
        }

        var category = CategorizeSource(source);

        // Context: wszystkie properties które nie są structural noise
        var context = new Dictionary<string, string>();
        foreach (var (key, value) in logEvent.Properties)
        {
            if (key is "SourceContext" or "RequestId" or "RequestPath" or "ConnectionId"
                or "ActionId" or "ActionName" or "HostingRequestStartingLog"
                or "HostingRequestFinishedLog")
                continue;
            var rendered = value.ToString().Trim('"');
            if (rendered.Length > 500) rendered = rendered[..500] + "…";
            context[key] = rendered;
        }

        var stackTrace = logEvent.Exception?.ToString();
        if (stackTrace is not null && stackTrace.Length > opts.MaxStackTraceLength)
            stackTrace = stackTrace[..opts.MaxStackTraceLength] + "\n…[truncated]";

        return new SystemEvent
        {
            Severity = MapLevel(logEvent.Level),
            Source = source,
            Category = category,
            Message = logEvent.RenderMessage(),
            ExceptionType = logEvent.Exception?.GetType().FullName,
            ExceptionMessage = logEvent.Exception?.Message,
            StackTrace = stackTrace,
            Context = context,
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            CreatedAt = logEvent.Timestamp.UtcDateTime
        };
    }

    private static SystemEventSeverity MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => SystemEventSeverity.Debug,
        LogEventLevel.Debug => SystemEventSeverity.Debug,
        LogEventLevel.Information => SystemEventSeverity.Info,
        LogEventLevel.Warning => SystemEventSeverity.Warning,
        LogEventLevel.Error => SystemEventSeverity.Error,
        LogEventLevel.Fatal => SystemEventSeverity.Critical,
        _ => SystemEventSeverity.Info
    };

    /// <summary>Heurystyka kategoryzacji na podstawie nazwy klasy.</summary>
    private static string CategorizeSource(string source) => source switch
    {
        _ when source.Contains("Mongo", StringComparison.OrdinalIgnoreCase) => "Database",
        _ when source.Contains("MediaMtx", StringComparison.OrdinalIgnoreCase) => "Camera",
        _ when source.Contains("Ffmpeg", StringComparison.OrdinalIgnoreCase) => "Camera",
        _ when source.Contains("Camera", StringComparison.OrdinalIgnoreCase) => "Camera",
        _ when source.Contains("Storage", StringComparison.OrdinalIgnoreCase) => "Storage",
        _ when source.Contains("FileStore", StringComparison.OrdinalIgnoreCase) => "Storage",
        _ when source.Contains("Http", StringComparison.OrdinalIgnoreCase) => "Network",
        _ when source.Contains("Auth", StringComparison.OrdinalIgnoreCase) => "Security",
        _ when source.Contains("License", StringComparison.OrdinalIgnoreCase) => "License",
        _ when source.Contains("Program", StringComparison.OrdinalIgnoreCase) => "Startup",
        _ when source.Contains("Host", StringComparison.OrdinalIgnoreCase) => "Startup",
        _ => "Application"
    };

    public void Dispose()
    {
        _stopCts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { /* swallow */ }
        _signal.Dispose();
        _stopCts.Dispose();
    }
}
