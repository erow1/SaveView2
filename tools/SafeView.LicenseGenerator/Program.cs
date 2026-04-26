using System.CommandLine;
using System.Globalization;
using SafeView.Licensing.Crypto;
using SafeView.Licensing.Model;
using SafeView.Licensing.Modules;

// ────────────────────────────────────────────────────────────────────────────────
// SafeView License Generator CLI
//   issue  — wydaje nowy plik .lic
//   show   — deszyfruje i wypisuje zawartość pliku .lic
//   verify — sprawdza integralność (HMAC/AES) i wygaśnięcie
// ────────────────────────────────────────────────────────────────────────────────

var root = new RootCommand("SafeView license generator / inspector");

// ─── issue ──────────────────────────────────────────────────────────────────────
var customerIdOpt = new Option<string>("--customer-id") { IsRequired = true, Description = "Stabilny identyfikator klienta" };
var customerNameOpt = new Option<string>("--customer-name") { IsRequired = true, Description = "Nazwa klienta" };
var daysOpt = new Option<int>("--days", () => 365) { Description = "Liczba dni ważności od dziś" };
var modulesOpt = new Option<string[]>("--modules") { AllowMultipleArgumentsPerToken = true, Description = "Kody modułów, np. MOD.PPE MOD.ZONES. Użyj 'ALL' aby włączyć wszystkie." };
var limitsOpt = new Option<string[]>("--limit") { AllowMultipleArgumentsPerToken = true, Description = "Limity w formacie klucz=wartość, np. cameras=16 users=25" };
var notesOpt = new Option<string?>("--notes") { Description = "Opcjonalne notatki" };
var outputOpt = new Option<FileInfo>("--output") { IsRequired = true, Description = "Ścieżka wynikowa .lic" };

var issueCmd = new Command("issue", "Wydaje nowy plik licencji");
issueCmd.AddOption(customerIdOpt);
issueCmd.AddOption(customerNameOpt);
issueCmd.AddOption(daysOpt);
issueCmd.AddOption(modulesOpt);
issueCmd.AddOption(limitsOpt);
issueCmd.AddOption(notesOpt);
issueCmd.AddOption(outputOpt);
issueCmd.SetHandler(context =>
{
    var customerId = context.ParseResult.GetValueForOption(customerIdOpt)!;
    var customerName = context.ParseResult.GetValueForOption(customerNameOpt)!;
    var days = context.ParseResult.GetValueForOption(daysOpt);
    var modules = context.ParseResult.GetValueForOption(modulesOpt) ?? [];
    var limits = context.ParseResult.GetValueForOption(limitsOpt) ?? [];
    var notes = context.ParseResult.GetValueForOption(notesOpt);
    var output = context.ParseResult.GetValueForOption(outputOpt)!;

    var resolvedModules = ResolveModules(modules);
    var resolvedLimits = ParseLimits(limits);

    var lic = new LicenseFile
    {
        CustomerId = customerId,
        CustomerName = customerName,
        IssuedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(days),
        Modules = resolvedModules.ToList(),
        Limits = resolvedLimits,
        Notes = notes
    };

    var svc = new LicenseCryptoService();
    var text = svc.Encrypt(lic);

    var dir = output.Directory;
    if (dir is not null && !dir.Exists) dir.Create();
    File.WriteAllText(output.FullName, text);

    Console.WriteLine($"[OK] Wydano licencję dla '{customerName}' ({customerId})");
    Console.WriteLine($"     Wygasa: {lic.ExpiresAt:yyyy-MM-dd HH:mm} UTC ({days} dni)");
    Console.WriteLine($"     Moduły: {string.Join(", ", resolvedModules)}");
    if (resolvedLimits.Count > 0)
        Console.WriteLine($"     Limity: {string.Join(", ", resolvedLimits.Select(kv => $"{kv.Key}={kv.Value}"))}");
    Console.WriteLine($"     Plik  : {output.FullName}");
    Console.WriteLine($"     FP    : {LicenseCryptoService.ComputeFingerprint(text)}");
    context.ExitCode = 0;
});

// ─── show ───────────────────────────────────────────────────────────────────────
var showFileArg = new Argument<FileInfo>("file", "Ścieżka do pliku .lic");
var showCmd = new Command("show", "Deszyfruje i wypisuje zawartość pliku .lic");
showCmd.AddArgument(showFileArg);
showCmd.SetHandler(context =>
{
    var file = context.ParseResult.GetValueForArgument(showFileArg);
    if (!file.Exists) { Console.Error.WriteLine($"Nie znaleziono: {file.FullName}"); context.ExitCode = 2; return; }

    var text = File.ReadAllText(file.FullName);
    var svc = new LicenseCryptoService();
    try
    {
        var lic = svc.Decrypt(text);
        Console.WriteLine($"Customer    : {lic.CustomerName} ({lic.CustomerId})");
        Console.WriteLine($"IssuedAt    : {lic.IssuedAt:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"ExpiresAt   : {lic.ExpiresAt:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"Modules     : {string.Join(", ", lic.Modules)}");
        Console.WriteLine($"Limits      : {string.Join(", ", lic.Limits.Select(kv => $"{kv.Key}={kv.Value}"))}");
        if (!string.IsNullOrWhiteSpace(lic.Notes))
            Console.WriteLine($"Notes       : {lic.Notes}");
        Console.WriteLine($"Fingerprint : {LicenseCryptoService.ComputeFingerprint(text)}");
        context.ExitCode = 0;
    }
    catch (InvalidLicenseException ex)
    {
        Console.Error.WriteLine($"[BŁĄD] {ex.Message}");
        context.ExitCode = 3;
    }
});

// ─── verify ─────────────────────────────────────────────────────────────────────
var verifyFileArg = new Argument<FileInfo>("file", "Ścieżka do pliku .lic");
var verifyCmd = new Command("verify", "Weryfikuje integralność i ważność pliku .lic");
verifyCmd.AddArgument(verifyFileArg);
verifyCmd.SetHandler(context =>
{
    var file = context.ParseResult.GetValueForArgument(verifyFileArg);
    if (!file.Exists) { Console.Error.WriteLine($"Nie znaleziono: {file.FullName}"); context.ExitCode = 2; return; }

    var text = File.ReadAllText(file.FullName);
    var svc = new LicenseCryptoService();
    try
    {
        var lic = svc.Decrypt(text);
        var now = DateTime.UtcNow;
        var valid = lic.ExpiresAt > now;
        Console.WriteLine("HMAC/AES    : OK");
        Console.WriteLine($"Expiry      : {lic.ExpiresAt:yyyy-MM-dd} ({(valid ? "VALID" : "EXPIRED")})");
        Console.WriteLine($"Fingerprint : {LicenseCryptoService.ComputeFingerprint(text)}");
        context.ExitCode = valid ? 0 : 1;
    }
    catch (InvalidLicenseException ex)
    {
        Console.Error.WriteLine($"[INVALID] {ex.Message}");
        context.ExitCode = 3;
    }
});

root.AddCommand(issueCmd);
root.AddCommand(showCmd);
root.AddCommand(verifyCmd);

return await root.InvokeAsync(args).ConfigureAwait(false);

// ─── helpers ────────────────────────────────────────────────────────────────────
static IReadOnlyList<string> ResolveModules(string[] raw)
{
    if (raw.Length == 0) return new[] { LicenseModules.Core };
    if (raw.Any(m => string.Equals(m, "ALL", StringComparison.OrdinalIgnoreCase)))
        return LicenseModules.All;

    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LicenseModules.Core };
    foreach (var m in raw)
    {
        var code = m.Trim();
        if (!LicenseModules.All.Contains(code, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Nieznany kod modułu: {code}. Dostępne: {string.Join(", ", LicenseModules.All)}");
        set.Add(code);
    }
    return set.ToList();
}

static Dictionary<string, long> ParseLimits(string[] raw)
{
    var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in raw)
    {
        var idx = item.IndexOf('=', StringComparison.Ordinal);
        if (idx <= 0)
            throw new ArgumentException($"Oczekiwano klucz=wartość, otrzymano: {item}");
        var key = item[..idx].Trim();
        var valStr = item[(idx + 1)..].Trim();
        if (!long.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
            throw new ArgumentException($"Niepoprawna wartość liczbowa: {valStr}");
        dict[key] = val;
    }
    return dict;
}
