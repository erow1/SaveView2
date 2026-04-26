using System.Runtime.InteropServices;

namespace SafeView.Cameras;

/// <summary>
/// Rozwiązuje ścieżki do bundled binarek (ffmpeg, mediamtx) dostarczanych w
/// katalogu <c>runtime/binaries/{rid}/</c> — dzięki temu SafeView nie wymaga
/// instalacji tych narzędzi w systemie.
/// </summary>
public static class BundledBinaries
{
    /// <summary>
    /// Runtime Identifier aktualnej platformy — zgodny z .NET RID
    /// (np. <c>osx-arm64</c>, <c>linux-x64</c>, <c>win-x64</c>).
    /// Zwraca null dla nieobsługiwanych kombinacji OS/arch.
    /// </summary>
    public static string? CurrentRid
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null
            };
            if (arch is null) return null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
            return null;
        }
    }

    /// <summary>
    /// Rozwiązuje absolutną ścieżkę bundled binarki (np. <c>ffmpeg</c> lub
    /// <c>mediamtx</c>) w katalogu <c>runtime/binaries/{rid}/</c>.
    /// Dodaje <c>.exe</c> automatycznie na Windows.
    /// Zwraca null gdy platforma nieobsługiwana lub plik nie istnieje.
    /// </summary>
    public static string? ResolvePath(string binaryName)
    {
        var rid = CurrentRid;
        if (rid is null) return null;

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? binaryName + ".exe"
            : binaryName;

        // Szukamy w kilku typowych lokalizacjach (repo root, obok binarki, obok cwd)
        foreach (var baseDir in CandidateBaseDirectories())
        {
            var candidate = Path.Combine(baseDir, "runtime", "binaries", rid, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Jeśli <paramref name="configured"/> jest ustawione — zwraca bez zmian.
    /// Inaczej próbuje bundled. Ostatecznie wraca do <paramref name="fallback"/>
    /// (nazwa do rozwiązania przez system PATH).
    /// </summary>
    public static string Resolve(string? configured, string binaryName, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return ResolvePath(binaryName) ?? fallback;
    }

    private static IEnumerable<string> CandidateBaseDirectories()
    {
        // 1) Katalog binarki uruchomieniowej (standardowo bin/Debug/net10.0/...)
        var appBase = AppContext.BaseDirectory;
        yield return appBase;

        // 2) Idź w górę szukając folderu runtime/binaries (przydatne podczas dev-runów,
        //    gdzie cwd / bin są kilka poziomów niżej niż repo root)
        var dir = new DirectoryInfo(appBase);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "runtime", "binaries")))
            {
                yield return dir.FullName;
                yield break;
            }
        }

        // 3) Current working directory — przy `dotnet run` startujemy z repo roota
        yield return Directory.GetCurrentDirectory();
    }
}
