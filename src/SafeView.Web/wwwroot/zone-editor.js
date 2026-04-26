// SafeView — Zone polygon editor bridge (Blazor <-> SVG click)
window.safeviewZoneEditor = (function () {
    const handlers = new Map();

    function attach(canvasId, dotNetRef) {
        const root = document.getElementById(canvasId);
        if (!root) return;
        const svg = root.querySelector('svg.sv-zone-canvas');
        if (!svg) return;

        const onClick = (ev) => {
            const rect = svg.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            const nx = (ev.clientX - rect.left) / rect.width;
            const ny = (ev.clientY - rect.top) / rect.height;
            dotNetRef.invokeMethodAsync('AddPoint', nx, ny);
        };

        svg.addEventListener('click', onClick);
        handlers.set(canvasId, { svg, onClick });
    }

    function detach(canvasId) {
        const h = handlers.get(canvasId);
        if (!h) return;
        try { h.svg.removeEventListener('click', h.onClick); } catch {}
        handlers.delete(canvasId);
    }

    return { attach, detach };
})();
