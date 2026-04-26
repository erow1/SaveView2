// Flow topology graph — Cytoscape.js + SignalR live events.
// Pipeline: Camera -> ROI -> Model / Zone -> Trigger -> Action.
// Styl spójny z resztą aplikacji (MudBlazor dark, sv-card, JetBrains Mono, akcentowe kolory).
// Bez emoji, bez ikon unicode.

(function () {
    let cy = null;
    let hub = null;
    let dotnetRef = null;
    let themeObserver = null;

    // Akcentowe kolory per typ (pasują do chipów w innych miejscach aplikacji)
    const ACCENT = {
        camera: '#4DA6FF',
        roi: '#7AD6FF',
        zone: '#B48EFF',
        trigger: '#F5A524',
        action: '#2EC27E',
        model: '#9A9A9A'
    };

    const TYPE_LABEL = {
        camera: 'KAMERA',
        roi: 'ROI',
        zone: 'STREFA',
        trigger: 'TRIGGER',
        action: 'AKCJA',
        model: 'MODEL'
    };

    // Cytoscape styluje label jako jeden tekst; multiline przez \n.
    function nodeLabel(ele) {
        const type = ele.data('type');
        const name = ele.data('label') || '';
        return `${TYPE_LABEL[type] || type.toUpperCase()}\n${name}`;
    }

    // Motyw odczytywany z klasy body (theme-light / theme-dark).
    // Kolory reszty wynikają stąd — bez twardo-wpisanych ciemnych wartości.
    function currentTheme() {
        const isLight = document.body && document.body.classList.contains('theme-light');
        if (isLight) {
            return {
                // Tła kafli biorą kolor akcentu (typu) z niską alfą — zobacz nodeBgOpacity niżej.
                nodeBgOpacity: 0.12,
                nodeBgSelectedOpacity: 0.22,
                nodeFlashOpacity: 0.40,
                label: '#111111',
                labelMuted: '#555555',
                edge: 'rgba(0,0,0,0.28)',
                edgeArrow: 'rgba(0,0,0,0.42)',
                edgeDashed: 'rgba(0,0,0,0.18)',
                edgeDashedArrow: 'rgba(0,0,0,0.3)'
            };
        }
        return {
            nodeBgOpacity: 0.22,
            nodeBgSelectedOpacity: 0.35,
            nodeFlashOpacity: 0.55,
            label: '#E6E6E6',
            labelMuted: '#9A9A9A',
            edge: 'rgba(200,200,210,0.22)',
            edgeArrow: 'rgba(200,200,210,0.35)',
            edgeDashed: 'rgba(154,154,154,0.2)',
            edgeDashedArrow: 'rgba(154,154,154,0.3)'
        };
    }

    function styleSheet() {
        const t = currentTheme();
        return [
            {
                selector: 'node',
                style: {
                    // Tło = kolor akcentu typu z niską alfą (subtelne tintowanie)
                    'background-color': (ele) => ACCENT[ele.data('type')] || '#888',
                    'background-opacity': t.nodeBgOpacity,
                    'label': nodeLabel,
                    'color': t.label,
                    'font-size': '12px',
                    'font-family': '"JetBrains Mono", ui-monospace, monospace',
                    'font-weight': 600,
                    'text-valign': 'center',
                    'text-halign': 'center',
                    'text-wrap': 'wrap',
                    'text-max-width': '180px',
                    'text-margin-y': 0,
                    'line-height': 1.35,
                    'width': 'label',
                    'height': 'label',
                    'padding-top': '14px',
                    'padding-bottom': '14px',
                    'padding-left': '18px',
                    'padding-right': '18px',
                    'shape': 'round-rectangle',
                    'border-width': 2,
                    'border-color': (ele) => ACCENT[ele.data('type')] || '#888',
                    'border-opacity': 1
                }
            },
            {
                // Drobny "typ" w pierwszej linii — niestety cytoscape nie stylizuje per-linia,
                // więc robimy to przez małą czcionkę całą. Second line jest pod spodem via text-wrap.
                selector: 'node[enabled = 0]',
                style: {
                    'opacity': 0.45,
                    'border-style': 'dashed'
                }
            },
            {
                selector: 'node:selected',
                style: {
                    'border-width': 3,
                    'background-opacity': t.nodeBgSelectedOpacity
                }
            },
            // Edge — domyślnie cienki, subtelny
            {
                selector: 'edge',
                style: {
                    'width': 1.2,
                    'line-color': t.edge,
                    'target-arrow-color': t.edgeArrow,
                    'target-arrow-shape': 'triangle',
                    'curve-style': 'bezier',
                    'arrow-scale': 0.9,
                    'line-dash-pattern': [6, 6],
                    'line-dash-offset': 0
                }
            },
            {
                // roi → model: przerywana (pomocnicza, nie główny flow)
                selector: 'edge[kind = "roi-model"], edge[kind = "roi-proposer"]',
                style: {
                    'line-style': 'dashed',
                    'line-color': t.edgeDashed,
                    'target-arrow-color': t.edgeDashedArrow
                }
            },
            // ── Stany live ─────────────────────────────────────────────
            {
                selector: 'edge.flowing',
                style: {
                    'line-style': 'dashed',
                    'line-color': '#F5A524',
                    'target-arrow-color': '#F5A524',
                    'width': 2.5
                }
            },
            {
                selector: 'edge.success',
                style: {
                    'line-color': '#2EC27E',
                    'target-arrow-color': '#2EC27E',
                    'width': 2.5
                }
            },
            {
                selector: 'edge.error',
                style: {
                    'line-color': '#FF5964',
                    'target-arrow-color': '#FF5964',
                    'width': 2.5
                }
            },
            {
                selector: 'node.flash',
                style: {
                    'background-opacity': t.nodeFlashOpacity,
                    'border-width': 3
                }
            }
        ];
    }

    function buildElements(topology) {
        const nodes = (topology.nodes || []).map(n => ({
            data: { id: n.id, type: n.type, label: n.label, enabled: n.enabled ? 1 : 0, color: n.color || null, raw: n }
        }));
        const nodeIds = new Set(nodes.map(n => n.data.id));
        const edges = (topology.edges || []).map((e, i) => ({
            data: { id: `e${i}`, source: e.source, target: e.target, kind: e.kind }
        })).filter(e => nodeIds.has(e.data.source) && nodeIds.has(e.data.target));
        return [...nodes, ...edges];
    }

    // Smooth pulse — delikatne mignięcie z powrotem do normy.
    function pulseNode(node) {
        if (!node || node.length === 0) return;
        node.addClass('flash');
        setTimeout(() => node.removeClass('flash'), 900);
    }

    // Animowany "przepływ" po edge — dashowe kreski ślizgają się od source do target.
    // Używa line-dash-offset, krok co ~16ms (60fps) przez 1200ms, potem reset.
    const activeFlows = new Map();

    function animateEdgeFlow(edge, klass, durationMs = 1200) {
        if (!edge || edge.length === 0) return;
        const edgeId = edge.id();

        // Jeśli już jest animacja na tym edge — zrestartuj
        if (activeFlows.has(edgeId)) {
            const prev = activeFlows.get(edgeId);
            cancelAnimationFrame(prev.raf);
            clearTimeout(prev.timer);
            edge.removeClass('flowing success error');
        }

        edge.addClass(klass);

        const start = performance.now();
        const step = (now) => {
            const t = now - start;
            if (t > durationMs) {
                edge.removeClass('flowing success error');
                edge.style('line-dash-offset', 0);
                activeFlows.delete(edgeId);
                return;
            }
            // Ujemny offset = kreski lecą w kierunku target-a
            const offset = -((t / 50) % 12);
            edge.style('line-dash-offset', offset);
            state.raf = requestAnimationFrame(step);
        };
        const state = { raf: requestAnimationFrame(step), timer: null };
        activeFlows.set(edgeId, state);
    }

    function findEdge(sourcePrefix, sourceId, targetPrefix, targetId) {
        if (!cy) return cy;
        const sid = `${sourcePrefix}:${sourceId}`;
        const tid = `${targetPrefix}:${targetId}`;
        return cy.edges().filter(e => e.data('source') === sid && e.data('target') === tid);
    }

    function findNode(prefix, id) {
        if (!cy) return null;
        return cy.getElementById(`${prefix}:${id}`);
    }

    // ── Handlery eventów SignalR ─────────────────────────────────────────────
    function onTriggerFired(payload) {
        if (!cy) return;
        const edge = findEdge('zon', payload.zoneId, 'trg', payload.triggerId);
        if (edge) animateEdgeFlow(edge, 'flowing');
        pulseNode(findNode('trg', payload.triggerId));
    }

    function onVllmRejected(payload) {
        if (!cy) return;
        const edge = findEdge('zon', payload.zoneId, 'trg', payload.triggerId);
        if (edge) animateEdgeFlow(edge, 'error');
    }

    function onActionExecuted(payload) {
        if (!cy) return;
        const edge = findEdge('trg', payload.triggerId, 'act', payload.actionId);
        const klass = payload.status === 'Success' ? 'success'
            : (payload.status === 'Failed' || payload.status === 'RateLimited') ? 'error'
            : 'flowing';
        if (edge) animateEdgeFlow(edge, klass);
        pulseNode(findNode('act', payload.actionId));
    }

    // ── Public API ───────────────────────────────────────────────────────────
    window.svFlowInit = async function (containerId, topology, dotnet) {
        dotnetRef = dotnet;
        const container = document.getElementById(containerId);
        if (!container) return;

        if (typeof cytoscape.use === 'function' && typeof cytoscapeDagre !== 'undefined') {
            try { cytoscape.use(cytoscapeDagre); } catch { /* already registered */ }
        }

        cy = cytoscape({
            container,
            elements: buildElements(topology),
            style: styleSheet(),
            layout: {
                name: 'dagre',
                rankDir: 'LR',
                nodeSep: 36,
                edgeSep: 12,
                rankSep: 100,
                fit: true,
                padding: 40
            },
            wheelSensitivity: 0.2,
            minZoom: 0.25,
            maxZoom: 2.5,
            boxSelectionEnabled: false,
            autoungrabify: false
        });

        cy.on('tap', 'node', (evt) => {
            const n = evt.target.data('raw');
            if (!n || !dotnetRef) return;
            const parts = n.id.split(':');
            const type = parts[0] === 'cam' ? 'camera'
                : parts[0] === 'roi' ? 'roi'
                : parts[0] === 'zon' ? 'zone'
                : parts[0] === 'trg' ? 'trigger'
                : parts[0] === 'act' ? 'action'
                : parts[0] === 'mdl' ? 'model' : parts[0];
            dotnetRef.invokeMethodAsync('OnNodeClick', type, parts[1]).catch(() => { });
        });

        // Reaguj na zmianę motywu (toggle light/dark) — body.class się zmienia,
        // przepinamy paletę w cytoscape bez restartu pollera / hub-a.
        try {
            if (themeObserver) themeObserver.disconnect();
            themeObserver = new MutationObserver(() => {
                if (cy) cy.style().fromJson(styleSheet()).update();
            });
            themeObserver.observe(document.body, { attributes: true, attributeFilter: ['class'] });
        } catch { /* no-op */ }

        await connectHub();
    };

    window.svFlowRelayout = function () {
        if (!cy) return;
        cy.layout({
            name: 'dagre', rankDir: 'LR',
            nodeSep: 36, edgeSep: 12, rankSep: 100,
            fit: true, padding: 40
        }).run();
    };

    window.svFlowFit = function () {
        if (cy) cy.fit(undefined, 40);
    };

    window.svFlowDestroy = async function () {
        for (const [, state] of activeFlows) {
            if (state.raf) cancelAnimationFrame(state.raf);
        }
        activeFlows.clear();
        if (themeObserver) { themeObserver.disconnect(); themeObserver = null; }
        if (hub) {
            try { await hub.stop(); } catch { }
            hub = null;
        }
        if (cy) { cy.destroy(); cy = null; }
    };

    async function connectHub() {
        if (typeof signalR === 'undefined') return;
        try {
            hub = new signalR.HubConnectionBuilder()
                .withUrl('/hubs/flow')
                .withAutomaticReconnect()
                .build();

            hub.on('TriggerFired', onTriggerFired);
            hub.on('VllmRejected', onVllmRejected);
            hub.on('ActionExecuted', onActionExecuted);
            hub.on('SnapshotCaptured', () => { /* rezerwa pod puls kamery przy capture */ });

            await hub.start();
        } catch (err) {
            console.warn('FlowHub connection failed:', err);
        }
    }
})();
