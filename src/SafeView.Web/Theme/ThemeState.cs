namespace SafeView.Web.Theme;

/// <summary>
/// Scoped per-circuit stan motywu (dark/light). Toggle powiadamia subskrybentów
/// (MainLayout) żeby przerenderowali MudThemeProvider. Persist w localStorage
/// jest realizowany w MainLayout via JS interop.
/// </summary>
public sealed class ThemeState
{
    public bool IsDark { get; private set; } = true;

    public event Action? Changed;

    public void Set(bool isDark)
    {
        if (IsDark == isDark) return;
        IsDark = isDark;
        Changed?.Invoke();
    }

    public void Toggle() => Set(!IsDark);
}
