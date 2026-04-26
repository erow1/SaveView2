// Persistuje wybór trybu jasny/ciemny w localStorage i ustawia klasę na <body>
// (aby CSS overrides mogły się przełączać).
window.safeviewTheme = {
    get: function () {
        try {
            const v = localStorage.getItem('safeview.theme');
            return v === 'light' ? 'light' : (v === 'dark' ? 'dark' : null);
        } catch { return null; }
    },
    set: function (mode) {
        try { localStorage.setItem('safeview.theme', mode); } catch { /* ignore */ }
        this.apply(mode);
    },
    apply: function (mode) {
        const body = document.body;
        if (!body) return;
        body.classList.remove('theme-light', 'theme-dark');
        body.classList.add(mode === 'light' ? 'theme-light' : 'theme-dark');
    },
    // preferencja systemu — tylko gdy brak zapisanej
    prefersDark: function () {
        try { return window.matchMedia('(prefers-color-scheme: dark)').matches; }
        catch { return true; }
    }
};

// na starcie od razu ustaw body class (zanim Blazor dociągnie)
(function () {
    const t = window.safeviewTheme;
    const saved = t.get();
    const mode = saved ?? (t.prefersDark() ? 'dark' : 'light');
    t.apply(mode);
})();
