// Monitor page — multi-tile live grid.
// Każdy kafel to niezależny poller (snapshot + /api/monitor) z własnym session tokenem,
// AbortController i overlay (SVG + HTML labels). Globalne toggles są współdzielone.

(function () {
    // globalne toggles — wspólne dla wszystkich kafli
    let toggles = { rois: true, zones: true, detections: true, distances: false, maxPairs: 3 };

    // stan per-kafel: { token, timer, abort, ctx }
    const tiles = new Map();

    function svgNs(tag, attrs) {
        const el = document.createElementNS('http://www.w3.org/2000/svg', tag);
        for (const k in attrs) el.setAttribute(k, attrs[k]);
        return el;
    }

    function projectFoot(d, H) {
        if (!H) return null;
        const px = d.x + d.width / 2;
        const py = d.y + d.height;
        const w = H.m20 * px + H.m21 * py + H.m22;
        if (Math.abs(w) < 1e-9) return null;
        return { x: (H.m00 * px + H.m01 * py + H.m02) / w, y: (H.m10 * px + H.m11 * py + H.m12) / w, pixelX: px, pixelY: py };
    }

    function makeLabel(nx, ny, nw, color, text, position) {
        const el = document.createElement('div');
        const nearTop = ny < 0.05;
        const labelBelow = position === 'inside' || nearTop;
        el.style.cssText = [
            'position:absolute',
            `left:${nx * 100}%`,
            labelBelow ? `top:${ny * 100}%` : `top:calc(${ny * 100}% - 18px)`,
            `max-width:${Math.max(nw * 100, 10)}%`,
            'height:18px',
            'display:flex',
            'align-items:center',
            'padding:0 5px',
            `background:${color}`,
            'color:#fff',
            'font:600 9px/1 "JetBrains Mono", ui-monospace, monospace',
            'letter-spacing:0.02em',
            'white-space:nowrap',
            'overflow:hidden',
            'text-overflow:ellipsis',
            'border-radius:2px 2px 0 0',
            'pointer-events:none'
        ].join(';');
        el.textContent = text;
        return el;
    }

    function hexA(hex, a) {
        if (!hex || hex[0] !== '#' || hex.length !== 7) return `rgba(46,194,126,${a})`;
        const r = parseInt(hex.slice(1, 3), 16);
        const g = parseInt(hex.slice(3, 5), 16);
        const b = parseInt(hex.slice(5, 7), 16);
        return `rgba(${r},${g},${b},${a})`;
    }

    function renderOverlay(tileId, data) {
        const svg = document.getElementById(`sv-tile-svg-${tileId}`);
        const lbl = document.getElementById(`sv-tile-labels-${tileId}`);
        if (!svg || !lbl) return;
        while (svg.firstChild) svg.removeChild(svg.firstChild);
        while (lbl.firstChild) lbl.removeChild(lbl.firstChild);

        if (toggles.rois && data.rois) {
            for (const r of data.rois) {
                const color = r.color || '#4DA6FF';
                svg.appendChild(svgNs('rect', {
                    x: r.x, y: r.y, width: r.width, height: r.height,
                    fill: 'none', stroke: color, 'stroke-width': 1.2,
                    'stroke-dasharray': '4 3', 'vector-effect': 'non-scaling-stroke'
                }));
            }
        }

        if (toggles.zones && data.zones) {
            for (const z of data.zones) {
                const color = z.color || '#FF5964';
                const pts = z.points.map(p => `${p.x},${p.y}`).join(' ');
                svg.appendChild(svgNs('polygon', {
                    points: pts, fill: color, 'fill-opacity': '0.12',
                    stroke: color, 'stroke-width': 1.5, 'vector-effect': 'non-scaling-stroke'
                }));
            }
        }

        if (toggles.detections && data.detections) {
            for (const d of data.detections) {
                svg.appendChild(svgNs('rect', {
                    x: d.x, y: d.y, width: d.width, height: d.height,
                    fill: 'none', stroke: '#2EC27E', 'stroke-width': 1.5,
                    'vector-effect': 'non-scaling-stroke'
                }));
                const text = `${d.label} ${(d.confidence * 100).toFixed(0)}%`;
                lbl.appendChild(makeLabel(d.x, d.y, d.width, 'rgba(46,194,126,0.88)', text, 'auto'));
            }
        }

        if (toggles.distances && toggles.detections && data.homography && data.detections && data.detections.length >= 2) {
            const H = data.homography;
            const feet = data.detections.map(d => ({ det: d, foot: projectFoot(d, H) })).filter(p => p.foot);
            const pairs = [];
            for (let i = 0; i < feet.length; i++) {
                for (let j = i + 1; j < feet.length; j++) {
                    const a = feet[i], b = feet[j];
                    const dx = a.foot.x - b.foot.x;
                    const dy = a.foot.y - b.foot.y;
                    pairs.push({ a, b, distM: Math.sqrt(dx * dx + dy * dy) });
                }
            }
            pairs.sort((p, q) => p.distM - q.distM);
            const limited = pairs.slice(0, Math.max(1, toggles.maxPairs | 0));
            for (const { a, b, distM } of limited) {
                svg.appendChild(svgNs('line', {
                    x1: a.foot.pixelX, y1: a.foot.pixelY, x2: b.foot.pixelX, y2: b.foot.pixelY,
                    stroke: '#FFD600', 'stroke-width': 1.2, 'stroke-dasharray': '3 2',
                    'vector-effect': 'non-scaling-stroke', opacity: 0.75
                }));
                const midX = (a.foot.pixelX + b.foot.pixelX) / 2;
                const midY = (a.foot.pixelY + b.foot.pixelY) / 2;
                const d = document.createElement('div');
                d.style.cssText = `position:absolute;left:${midX * 100}%;top:${midY * 100}%;transform:translate(-50%,-50%);padding:1px 4px;background:rgba(255,214,0,0.88);color:#000;font:700 9px/1 'JetBrains Mono',monospace;border-radius:2px;pointer-events:none;white-space:nowrap;`;
                d.textContent = distM < 10 ? `${distM.toFixed(1)} m` : `${Math.round(distM)} m`;
                lbl.appendChild(d);
            }
        }
    }

    function setStatus(tileId, text) {
        const s = document.getElementById(`sv-tile-status-${tileId}`);
        if (s) s.textContent = text;
    }

    async function tick(tile) {
        if (tile.token !== tiles.get(tile.id)?.token) return; // obsolete
        tile.abort = new AbortController();
        try {
            const imgP = new Promise((resolve, reject) => {
                const p = new Image();
                p.onload = () => resolve(p);
                p.onerror = () => reject(new Error('img'));
                p.src = tile.ctx.snapshotUrl + '?t=' + Date.now();
            });
            const apiP = fetch(tile.ctx.apiUrl, { credentials: 'same-origin', signal: tile.abort.signal })
                .then(r => r.ok ? r.json() : Promise.reject(new Error('api ' + r.status)));
            const [preload, apiData] = await Promise.all([imgP, apiP]);
            if (tile.token !== tiles.get(tile.id)?.token) return;

            const img = document.getElementById(`sv-tile-img-${tile.id}`);
            if (img) img.src = preload.src;
            renderOverlay(tile.id, apiData);

            const dets = (apiData.detections || []).length;
            const cap = apiData.capturedAt || null;
            setStatus(tile.id, dets > 0 ? `${dets} det` : 'online');
            if (tile.ctx.dotnetRef) {
                try { await tile.ctx.dotnetRef.invokeMethodAsync('UpdateTileStats', tile.ctx.cameraId, dets, cap); }
                catch { }
            }
        } catch (err) {
            if (tile.token !== tiles.get(tile.id)?.token) return;
            if (err.name === 'AbortError') return;
            setStatus(tile.id, 'błąd');
        } finally {
            if (tile.token === tiles.get(tile.id)?.token) {
                tile.timer = setTimeout(() => tick(tile), tile.ctx.intervalMs);
            }
        }
    }

    // Uruchamia / restartuje kafel. Gdy kafel już istnieje — unieważnia starą sesję.
    window.svMonitorStartTile = function (tileId, snapshotUrl, apiUrl, intervalMs, cameraId, dotnetRef) {
        const prev = tiles.get(tileId);
        if (prev) {
            if (prev.timer) clearTimeout(prev.timer);
            if (prev.abort) { try { prev.abort.abort(); } catch { } }
        }
        const tile = {
            id: tileId,
            token: (prev?.token || 0) + 1,
            timer: null,
            abort: null,
            ctx: { snapshotUrl, apiUrl, intervalMs, cameraId, dotnetRef }
        };
        tiles.set(tileId, tile);
        setStatus(tileId, 'łączenie…');
        tick(tile);
    };

    window.svMonitorStopAll = function () {
        for (const [, tile] of tiles) {
            tile.token = -1;
            if (tile.timer) clearTimeout(tile.timer);
            if (tile.abort) { try { tile.abort.abort(); } catch { } }
        }
        tiles.clear();
    };

    window.svMonitorSetToggles = function (showRois, showZones, showDetections, showDistances, maxPairs) {
        toggles = {
            rois: !!showRois,
            zones: !!showZones,
            detections: !!showDetections,
            distances: !!showDistances,
            maxPairs: (maxPairs | 0) || 3
        };
        // wyczyść overlayy — następny tick każdego kafla odrysuje wg nowych toggles
        for (const [id] of tiles) {
            const svg = document.getElementById(`sv-tile-svg-${id}`);
            if (svg) while (svg.firstChild) svg.removeChild(svg.firstChild);
            const lbl = document.getElementById(`sv-tile-labels-${id}`);
            if (lbl) while (lbl.firstChild) lbl.removeChild(lbl.firstChild);
        }
    };
})();
