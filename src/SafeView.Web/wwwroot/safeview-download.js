// SafeView download helper — pobiera plik przez fetch (cookies same-origin), tworzy blob,
// inicjuje download w bieżącej karcie i zwraca po zakończeniu. Pozwala Blazor-owi
// pokazać modal "trwa generowanie..." podczas długiego renderu po stronie serwera
// i zamknąć go gdy serwer odpowiedział.
window.safeview = window.safeview || {};

window.safeview.downloadFile = async function (url, fallbackName) {
    try {
        const r = await fetch(url, { credentials: 'same-origin' });
        if (!r.ok) {
            return { ok: false, status: r.status, error: `HTTP ${r.status}` };
        }
        const blob = await r.blob();

        // Wyciągnij filename z Content-Disposition jeśli serwer go ustawił
        let filename = fallbackName || 'download';
        const cd = r.headers.get('content-disposition') || '';
        const match = cd.match(/filename\*?=(?:UTF-8'')?["']?([^"';]+)["']?/i);
        if (match && match[1]) filename = decodeURIComponent(match[1]);

        const objectUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = objectUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        // Daj browserowi sekundę na rozpoczęcie zapisu zanim zwolnimy URL
        setTimeout(() => URL.revokeObjectURL(objectUrl), 1500);
        return { ok: true, filename };
    } catch (e) {
        return { ok: false, error: String(e) };
    }
};
