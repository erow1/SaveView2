// SafeView — ROI rectangle drag-to-draw editor (Blazor <-> SVG drag)
//
// Użycie z Blazor:
//   await JS.InvokeVoidAsync('safeviewRoiEditor.attach', canvasId, dotNetRef);
//   // podczas dragowania JS wywołuje dotNetRef.OnRectanglePreview(x,y,w,h) (live, bez commit)
//   // na mouseup        JS wywołuje dotNetRef.OnRectangleDrawn(x,y,w,h)   (final commit)
//
// Wszystkie współrzędne znormalizowane do [0..1] względem rozmiaru SVG.
window.safeviewRoiEditor = (function () {
    const handlers = new Map();

    function attach(canvasId, dotNetRef) {
        const root = document.getElementById(canvasId);
        if (!root) return;
        const svg = root.querySelector('svg.sv-roi-canvas');
        if (!svg) return;

        let dragging = false;
        let startX = 0, startY = 0;

        function norm(ev) {
            const rect = svg.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return null;
            return {
                x: Math.max(0, Math.min(1, (ev.clientX - rect.left) / rect.width)),
                y: Math.max(0, Math.min(1, (ev.clientY - rect.top) / rect.height))
            };
        }

        function rectFrom(x1, y1, x2, y2) {
            return {
                x: Math.min(x1, x2),
                y: Math.min(y1, y2),
                w: Math.abs(x2 - x1),
                h: Math.abs(y2 - y1)
            };
        }

        const onDown = (ev) => {
            const p = norm(ev); if (!p) return;
            dragging = true;
            startX = p.x; startY = p.y;
            svg.style.cursor = 'crosshair';
            ev.preventDefault();
        };

        const onMove = (ev) => {
            if (!dragging) return;
            const p = norm(ev); if (!p) return;
            const r = rectFrom(startX, startY, p.x, p.y);
            try { dotNetRef.invokeMethodAsync('OnRectanglePreview', r.x, r.y, r.w, r.h); }
            catch {}
        };

        const onUp = (ev) => {
            if (!dragging) return;
            dragging = false;
            svg.style.cursor = 'crosshair';
            const p = norm(ev); if (!p) return;
            const r = rectFrom(startX, startY, p.x, p.y);
            // Ignoruj przypadkowe micro-klikniecia (poniżej 2% szerokości)
            if (r.w < 0.02 || r.h < 0.02) return;
            try { dotNetRef.invokeMethodAsync('OnRectangleDrawn', r.x, r.y, r.w, r.h); }
            catch {}
        };

        const onLeave = () => {
            if (dragging) dragging = false;
        };

        svg.addEventListener('mousedown', onDown);
        window.addEventListener('mousemove', onMove);
        window.addEventListener('mouseup', onUp);
        svg.addEventListener('mouseleave', onLeave);

        handlers.set(canvasId, { svg, onDown, onMove, onUp, onLeave });
    }

    function detach(canvasId) {
        const h = handlers.get(canvasId);
        if (!h) return;
        try {
            h.svg.removeEventListener('mousedown', h.onDown);
            window.removeEventListener('mousemove', h.onMove);
            window.removeEventListener('mouseup', h.onUp);
            h.svg.removeEventListener('mouseleave', h.onLeave);
        } catch {}
        handlers.delete(canvasId);
    }

    return { attach, detach };
})();
