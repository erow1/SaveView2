using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Licensing.Crypto;
using SafeView.Licensing.Model;
using SafeView.Licensing.Modules;

namespace SafeView.Licensing;

public sealed class LicenseService : ILicenseService
{
    private readonly IOptionsMonitor<LicenseOptions> _options;
    private readonly LicenseCryptoService _crypto;
    private readonly ILogger<LicenseService> _log;
    private readonly Lock _lock = new();

    private LicenseFile? _current;
    private string? _fingerprint;
    private LicenseStatus _status = LicenseStatus.MissingStatus("License not loaded yet.");

    public LicenseService(
        IOptionsMonitor<LicenseOptions> options,
        LicenseCryptoService crypto,
        ILogger<LicenseService> log)
    {
        _options = options;
        _crypto = crypto;
        _log = log;
    }

    public LicenseStatus Status { get { lock (_lock) return _status; } }
    public LicenseFile? Current { get { lock (_lock) return _current; } }
    public string? Fingerprint { get { lock (_lock) return _fingerprint; } }

    public event EventHandler<LicenseStatus>? StatusChanged;

    public async Task<LicenseStatus> LoadAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;

        // ── DEV BYPASS: sztuczna ważna licencja ze wszystkimi modułami ──
        if (opts.DevBypass)
        {
            var fake = new LicenseFile
            {
                CustomerId = "DEV",
                CustomerName = "DEV (bypass)",
                IssuedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddYears(10),
                Modules = LicenseModules.All.ToList(),
                Limits = new Dictionary<string, long>
                {
                    ["cameras"] = 9999,
                    ["users"] = 9999,
                    ["zones"] = 9999
                },
                Notes = "Development bypass — NOT FOR PRODUCTION"
            };
            var fakeStatus = new LicenseStatus
            {
                State = LicenseState.Valid,
                Message = "DEV BYPASS — wszystkie moduły aktywne",
                ExpiresAt = fake.ExpiresAt,
                DaysRemaining = (int)Math.Ceiling((fake.ExpiresAt - DateTime.UtcNow).TotalDays),
                CustomerName = fake.CustomerName,
                Fingerprint = "DEV-BYPASS",
                Modules = fake.Modules.AsReadOnly()
            };
            _log.LogWarning("LICENSE DEV BYPASS ACTIVE — all modules enabled, do not use in production");
            return Commit(fake, "DEV-BYPASS", fakeStatus);
        }

        var path = ResolvePath(opts.FilePath);
        LicenseStatus next;

        if (!File.Exists(path))
        {
            next = LicenseStatus.MissingStatus($"Brak pliku licencji: {path}");
            _log.LogWarning("License file missing at {Path}", path);
            return Commit(null, null, next);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            next = LicenseStatus.InvalidStatus($"Nie udało się odczytać pliku licencji: {ex.Message}");
            _log.LogError(ex, "Failed to read license file {Path}", path);
            return Commit(null, null, next);
        }

        LicenseFile license;
        try
        {
            license = _crypto.Decrypt(text);
        }
        catch (InvalidLicenseException ex)
        {
            next = LicenseStatus.InvalidStatus(ex.Message);
            _log.LogError(ex, "License validation failed");
            return Commit(null, null, next);
        }

        var fp = LicenseCryptoService.ComputeFingerprint(text);
        var now = DateTime.UtcNow;
        var daysRemaining = (int)Math.Ceiling((license.ExpiresAt - now).TotalDays);

        if (license.ExpiresAt < now)
        {
            next = new LicenseStatus
            {
                State = LicenseState.Expired,
                Message = $"Licencja wygasła {license.ExpiresAt:yyyy-MM-dd}.",
                ExpiresAt = license.ExpiresAt,
                DaysRemaining = daysRemaining,
                CustomerName = license.CustomerName,
                Fingerprint = fp,
                Modules = license.Modules.AsReadOnly()
            };
            _log.LogWarning("License expired on {ExpiresAt} (customer={Customer}, fp={Fingerprint})",
                license.ExpiresAt, license.CustomerName, fp);
            return Commit(license, fp, next);
        }

        next = new LicenseStatus
        {
            State = LicenseState.Valid,
            Message = "OK",
            ExpiresAt = license.ExpiresAt,
            DaysRemaining = daysRemaining,
            CustomerName = license.CustomerName,
            Fingerprint = fp,
            Modules = license.Modules.AsReadOnly()
        };
        _log.LogInformation(
            "License loaded: customer={Customer}, expires={ExpiresAt}, modules={ModuleCount}, fp={Fingerprint}",
            license.CustomerName, license.ExpiresAt, license.Modules.Count, fp);
        return Commit(license, fp, next);
    }

    public bool IsModuleEnabled(string moduleCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleCode);
        lock (_lock)
        {
            if (_current is null || _status.State != LicenseState.Valid) return false;
            // CORE zawsze dostępny, jeśli licencja jest ważna
            if (moduleCode == LicenseModules.Core) return true;
            return _current.Modules.Contains(moduleCode);
        }
    }

    public long GetLimit(string limitKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitKey);
        lock (_lock)
        {
            if (_current is null || _status.State != LicenseState.Valid) return -1;
            return _current.Limits.TryGetValue(limitKey, out var v) ? v : -1;
        }
    }

    private LicenseStatus Commit(LicenseFile? license, string? fp, LicenseStatus status)
    {
        bool changed;
        lock (_lock)
        {
            changed = _status.State != status.State || _fingerprint != fp;
            _current = license;
            _fingerprint = fp;
            _status = status;
        }
        if (changed)
        {
            try { StatusChanged?.Invoke(this, status); }
            catch (Exception ex) { _log.LogWarning(ex, "License StatusChanged handler threw"); }
        }
        return status;
    }

    private static string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}
