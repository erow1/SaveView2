using System.CommandLine;
using Microsoft.Extensions.Options;
using SafeView.Infrastructure.Configuration;
using SafeView.Infrastructure.Persistence;
using SafeView.Security.Passwords;

namespace SafeView.Admin;

/// <summary>
/// SafeView Admin CLI — operacje zarządcze na bazie produkcyjnej:
///   safeview-admin users list
///   safeview-admin users reset-password --username admin --new "..."
///   safeview-admin users unlock --username admin
///   safeview-admin api-keys list
///   safeview-admin db ping
///
/// Konfiguracja: ENV (Mongo__ConnectionString, Mongo__DatabaseName) lub --connection / --database.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var connOpt = new Option<string?>("--connection", () => null, "Mongo connection string (override).");
        var dbOpt = new Option<string?>("--database", () => null, "Mongo database name (override).");

        var root = new RootCommand("SafeView admin CLI") { connOpt, dbOpt };

        // ── users ────────────────────────────────────────────────────────────
        var usersCmd = new Command("users", "Operacje na użytkownikach");

        var listUsers = new Command("list", "Pokaż wszystkich użytkowników");
        listUsers.SetHandler(async (conn, db) =>
        {
            var ctx = BuildContext(conn, db);
            var repo = new MongoUserRepository(ctx);
            var users = await repo.ListAsync();
            Console.WriteLine($"{"Username",-20}{"Email",-30}{"Roles",-30}{"Status",-10}");
            Console.WriteLine(new string('─', 90));
            foreach (var u in users)
            {
                var status = u.IsLocked ? "LOCKED" : (u.IsActive ? "ACTIVE" : "OFF");
                Console.WriteLine($"{u.Username,-20}{u.Email,-30}{string.Join(",", u.Roles),-30}{status,-10}");
            }
        }, connOpt, dbOpt);

        var userOpt = new Option<string>("--username", "Login użytkownika") { IsRequired = true };
        var pwdOpt = new Option<string>("--new", "Nowe hasło") { IsRequired = true };

        var resetPwd = new Command("reset-password", "Zresetuj hasło użytkownika") { userOpt, pwdOpt };
        resetPwd.SetHandler(async (conn, db, username, newPwd) =>
        {
            if (newPwd.Length < 8) { Console.Error.WriteLine("Hasło musi mieć min. 8 znaków."); Environment.ExitCode = 2; return; }
            var ctx = BuildContext(conn, db);
            var repo = new MongoUserRepository(ctx);
            var user = await repo.FindByUsernameAsync(username);
            if (user is null) { Console.Error.WriteLine($"Brak użytkownika '{username}'."); Environment.ExitCode = 1; return; }
            user.PasswordHash = new Argon2PasswordHasher().Hash(newPwd);
            user.FailedLoginAttempts = 0;
            user.IsLocked = false;
            await repo.UpdateAsync(user);
            Console.WriteLine($"OK — hasło dla '{username}' zresetowane.");
        }, connOpt, dbOpt, userOpt, pwdOpt);

        var unlock = new Command("unlock", "Odblokuj zablokowane konto") { userOpt };
        unlock.SetHandler(async (conn, db, username) =>
        {
            var ctx = BuildContext(conn, db);
            var repo = new MongoUserRepository(ctx);
            var user = await repo.FindByUsernameAsync(username);
            if (user is null) { Console.Error.WriteLine($"Brak '{username}'."); Environment.ExitCode = 1; return; }
            user.IsLocked = false;
            user.FailedLoginAttempts = 0;
            await repo.UpdateAsync(user);
            Console.WriteLine($"OK — '{username}' odblokowany.");
        }, connOpt, dbOpt, userOpt);

        usersCmd.AddCommand(listUsers);
        usersCmd.AddCommand(resetPwd);
        usersCmd.AddCommand(unlock);

        // ── api-keys ─────────────────────────────────────────────────────────
        var keysCmd = new Command("api-keys", "Operacje na kluczach API");
        var listKeys = new Command("list", "Pokaż klucze API (bez sekretów)");
        listKeys.SetHandler(async (conn, db) =>
        {
            var ctx = BuildContext(conn, db);
            var repo = new MongoApiKeyRepository(ctx);
            var keys = await repo.ListAsync();
            Console.WriteLine($"{"Name",-25}{"Prefix",-12}{"Scopes",-50}{"Status",-10}{"LastUsed",-20}");
            Console.WriteLine(new string('─', 117));
            foreach (var k in keys)
            {
                var status = !k.Enabled ? "OFF"
                           : (k.ExpiresAt is { } e && e <= DateTime.UtcNow ? "EXPIRED" : "ACTIVE");
                Console.WriteLine($"{k.Name,-25}{k.Prefix + "…",-12}{string.Join(",", k.Scopes),-50}{status,-10}{k.LastUsedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—",-20}");
            }
        }, connOpt, dbOpt);
        keysCmd.AddCommand(listKeys);

        // ── db ───────────────────────────────────────────────────────────────
        var dbCmd = new Command("db", "Operacje na bazie");
        var pingCmd = new Command("ping", "Sprawdź łączność z Mongo");
        pingCmd.SetHandler(async (conn, db) =>
        {
            var ctx = BuildContext(conn, db);
            try
            {
                await ctx.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
                Console.WriteLine($"OK — Mongo OK ({ctx.Database.DatabaseNamespace.DatabaseName}).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL — {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, connOpt, dbOpt);
        dbCmd.AddCommand(pingCmd);

        root.AddCommand(usersCmd);
        root.AddCommand(keysCmd);
        root.AddCommand(dbCmd);

        return await root.InvokeAsync(args);
    }

    private static MongoContext BuildContext(string? connOverride, string? dbOverride)
    {
        var opts = new MongoOptions
        {
            ConnectionString = connOverride
                ?? Environment.GetEnvironmentVariable("Mongo__ConnectionString")
                ?? "mongodb://localhost:27017",
            DatabaseName = dbOverride
                ?? Environment.GetEnvironmentVariable("Mongo__DatabaseName")
                ?? "safeview"
        };
        return new MongoContext(Options.Create(opts));
    }
}
