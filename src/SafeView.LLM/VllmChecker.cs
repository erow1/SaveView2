using System.Text.Json;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.LLM;
using SafeView.Domain.Detection;
using SafeView.Domain.Vllm;

namespace SafeView.LLM;

/// <summary>
/// Implementacja <see cref="IVllmChecker"/> przez <see cref="IChatClient"/>.
///
/// Faza A schema (z szablonami):
///   observations[] · alternative_explanations[] · severity · confidence · confirmed ·
///   image_quality · reason · recommended_action
///
/// Legacy backward-compat: parser akceptuje też stary schemat {confirmed, confidence, reason}
/// — brakujące pola mają wartości domyślne (severity=medium, quality=good, action=investigate).
///
/// Gate logic (gdy confirmed=true w odpowiedzi):
///  • confidence &lt; config.MinConfidenceToConfirm → NOT confirmed
///  • severity &lt; config.MinSeverity → NOT confirmed
///  • image_quality == poor AND config.RequireGoodImageQuality → NOT confirmed
/// </summary>
public sealed class VllmChecker : IVllmChecker
{
    private readonly IChatClientFactory _factory;
    private readonly ILogger<VllmChecker> _log;

    /// <summary>Domyślny schema jeśli config nie niesie własnego (legacy trigger bez TemplateId).</summary>
    private const string FallbackSchema = """
        {
          "type": "object",
          "required": ["confirmed", "confidence", "severity", "reason", "image_quality", "recommended_action"],
          "properties": {
            "observations": { "type": "array", "items": { "type": "string" } },
            "alternative_explanations": { "type": "array", "items": { "type": "string" } },
            "severity": { "type": "string", "enum": ["none", "low", "medium", "high", "critical"] },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "confirmed": { "type": "boolean" },
            "image_quality": { "type": "string", "enum": ["good", "acceptable", "poor"] },
            "reason": { "type": "string" },
            "recommended_action": { "type": "string", "enum": ["ignore", "monitor", "investigate", "alert", "emergency"] }
          }
        }
        """;

    public VllmChecker(IChatClientFactory factory, ILogger<VllmChecker> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<VllmCheckResult> CheckAsync(VllmCheckConfig config, ActionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        if (!config.Enabled)
            return new VllmCheckResult(true, 1.0, "VLLM check disabled", null,
                Severity: ResponseSeverity.Medium, Quality: ImageQuality.Good, Action: RecommendedAction.Investigate);

        var userPrompt = RenderTemplate(config.PromptTemplate, context);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, config.SystemPrompt ?? ""),
            new(
                ChatRole.User,
                userPrompt,
                ImagePath: config.IncludeFrame && File.Exists(context.FrameSnapshotPath)
                    ? context.FrameSnapshotPath
                    : null)
        };

        var schema = !string.IsNullOrWhiteSpace(config.SchemaJson) ? config.SchemaJson : FallbackSchema;
        var options = new ChatOptions(
            Model: config.ModelName,
            Temperature: 0.1,
            MaxTokens: 600,
            JsonSchema: schema);

        try
        {
            // Wybierz klienta: per-trigger provider albo default (IsDefault=true). Cache jest w factory.
            var chat = await _factory.GetForAsync(config.LlmProviderId, ct).ConfigureAwait(false);
            var resp = await chat.ChatAsync(messages, options, ct).ConfigureAwait(false);
            if (!resp.Success)
            {
                _log.LogWarning("VLLM check failed: {Err}", resp.ErrorMessage);
                return new VllmCheckResult(false, 0, $"LLM error: {resp.ErrorMessage}", resp.Content, IsError: true);
            }
            return Parse(resp.Content, config);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            // Brak skonfigurowanego providera (lub provider o ID nie istnieje + nie ma default).
            _log.LogWarning("VLLM check: brak skonfigurowanego providera LLM ({Msg})", ex.Message);
            return new VllmCheckResult(false, 0, $"LLM provider not configured: {ex.Message}", null, IsError: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "VLLM check exception");
            return new VllmCheckResult(false, 0, $"Exception: {ex.Message}", null, IsError: true);
        }
    }

    private VllmCheckResult Parse(string raw, VllmCheckConfig config)
    {
        var cleaned = ExtractJson(raw);
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var confirmed = root.TryGetProperty("confirmed", out var cEl) && cEl.ValueKind == JsonValueKind.True;
            var confidence = root.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out var d)
                ? Math.Clamp(d, 0, 1) : 0.0;
            var reason = root.TryGetProperty("reason", out var rEl) ? rEl.GetString() : null;
            var severity = ParseSeverity(root, "severity", ResponseSeverity.Medium);
            var quality = ParseQuality(root, "image_quality", ImageQuality.Good);
            var action = ParseAction(root, "recommended_action", RecommendedAction.Investigate);
            var observations = ReadStringArray(root, "observations");
            var alts = ReadStringArray(root, "alternative_explanations");

            // Gate 1: confidence poniżej progu → nie potwierdzaj
            if (confirmed && confidence < config.MinConfidenceToConfirm)
                return Fail(confidence, severity, quality, action, observations, alts,
                    $"Confidence {confidence:F2} < threshold {config.MinConfidenceToConfirm:F2}: {reason}", raw);

            // Gate 2: severity poniżej wymaganego minimum
            if (confirmed && severity < config.MinSeverity)
                return Fail(confidence, severity, quality, action, observations, alts,
                    $"Severity '{severity}' < required '{config.MinSeverity}': {reason}", raw);

            // Gate 3: jakość obrazu poor + wymagamy dobrej
            if (confirmed && quality == ImageQuality.Poor && config.RequireGoodImageQuality)
                return Fail(confidence, severity, quality, action, observations, alts,
                    $"Image quality poor, not alerting: {reason}", raw);

            return new VllmCheckResult(confirmed, confidence, reason, raw,
                Severity: severity, Quality: quality, Action: action,
                Observations: observations, AlternativeExplanations: alts);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "VLLM response is not valid JSON: {Raw}", raw);
            return new VllmCheckResult(false, 0, "Invalid JSON response from LLM", raw, IsError: true);
        }
    }

    private static VllmCheckResult Fail(double conf, ResponseSeverity sev, ImageQuality q, RecommendedAction act,
        IReadOnlyList<string>? obs, IReadOnlyList<string>? alts, string reason, string raw)
        => new(false, conf, reason, raw, Severity: sev, Quality: q, Action: act,
            Observations: obs, AlternativeExplanations: alts);

    private static ResponseSeverity ParseSeverity(JsonElement root, string name, ResponseSeverity fallback)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return fallback;
        return el.GetString()?.ToLowerInvariant() switch
        {
            "none" => ResponseSeverity.None,
            "low" => ResponseSeverity.Low,
            "medium" => ResponseSeverity.Medium,
            "high" => ResponseSeverity.High,
            "critical" => ResponseSeverity.Critical,
            _ => fallback
        };
    }

    private static ImageQuality ParseQuality(JsonElement root, string name, ImageQuality fallback)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return fallback;
        return el.GetString()?.ToLowerInvariant() switch
        {
            "good" => ImageQuality.Good,
            "acceptable" => ImageQuality.Acceptable,
            "poor" => ImageQuality.Poor,
            _ => fallback
        };
    }

    private static RecommendedAction ParseAction(JsonElement root, string name, RecommendedAction fallback)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return fallback;
        return el.GetString()?.ToLowerInvariant() switch
        {
            "ignore" => RecommendedAction.Ignore,
            "monitor" => RecommendedAction.Monitor,
            "investigate" => RecommendedAction.Investigate,
            "alert" => RecommendedAction.Alert,
            "emergency" => RecommendedAction.Emergency,
            _ => fallback
        };
    }

    private static List<string>? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) list.Add(s);
        return list.Count == 0 ? null : list;
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closing > 0) trimmed = trimmed[..closing];
            trimmed = trimmed.Trim();
        }
        if (!trimmed.StartsWith('{'))
        {
            var first = trimmed.IndexOf('{');
            var last = trimmed.LastIndexOf('}');
            if (first >= 0 && last > first)
                trimmed = trimmed[first..(last + 1)];
        }
        return trimmed;
    }

    private static string RenderTemplate(string template, ActionContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var labels = string.Join(", ", ctx.Detections.Select(d => d.Label).Distinct());
        var tod = ctx.OccurredAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return template
            .Replace("{camera}", ctx.CameraName, StringComparison.Ordinal)
            .Replace("{zone}", ctx.ZoneName, StringComparison.Ordinal)
            .Replace("{trigger}", ctx.TriggerName, StringComparison.Ordinal)
            .Replace("{labels}", labels, StringComparison.Ordinal)
            .Replace("{detections}", ctx.Detections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{time_of_day}", tod, StringComparison.Ordinal);
    }
}
