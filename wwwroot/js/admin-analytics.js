/* Analytics dashboard.
 *
 * Fetches GET /api/admin/analytics and renders every card from that one payload: metric tiles, an
 * SVG trend chart, an SVG world map, four breakdown lists and the recent-visits table. No charting
 * library — the shapes are simple enough that hand-built SVG is smaller than any dependency and
 * inherits the site's theme tokens for free.
 *
 * Everything is written with textContent / createElement rather than innerHTML: page paths,
 * referrers and Cloudflare city names all originate from request headers, so they are treated as
 * untrusted text throughout.
 */
(function () {
    'use strict';

    var API = '/api/admin/analytics';
    var DEFAULT_RANGE = '7d';

    // Equirectangular projection matching wwwroot/js/world-outline.js (1000 x 500 canvas).
    var MAP_W = 1000, MAP_H = 500;
    // Crop the poles: Antarctica and the empty Arctic waste half the frame otherwise.
    var MAP_VIEW = { top: lat2y(83), bottom: lat2y(-58) };
    // Largest marker radius, and the margin the viewBox gains so a marker near an edge — New Zealand
    // sits within 5° of the antimeridian — is not sliced in half by the frame.
    var MAP_MAX_R = 22, MAP_MARGIN = 26;

    var SVG_NS = 'http://www.w3.org/2000/svg';

    var state = {
        range: DEFAULT_RANGE,
        from: null,
        to: null,
        bots: false,
        facet: 'devices',
        report: null,
        inFlight: null,
    };

    var el = {};

    document.addEventListener('DOMContentLoaded', function () {
        el.error = document.getElementById('an-error');
        el.errorDetail = document.getElementById('an-error-detail');
        el.empty = document.getElementById('an-empty');
        el.stats = document.getElementById('an-stats');
        el.chart = document.getElementById('an-chart');
        el.chartTip = document.getElementById('an-chart-tip');
        el.granularity = document.getElementById('an-granularity');
        el.map = document.getElementById('an-map');
        el.mapNote = document.getElementById('an-map-note');
        el.countries = document.getElementById('an-countries');
        el.pages = document.getElementById('an-pages');
        el.referrers = document.getElementById('an-referrers');
        el.devices = document.getElementById('an-devices');
        el.recent = document.getElementById('an-recent');
        el.from = document.getElementById('an-from');
        el.to = document.getElementById('an-to');
        el.bots = document.getElementById('an-bots');

        wireControls();
        applyUrlState();
        load();
    });

    // ── Controls ────────────────────────────────────────────────────────────────

    function wireControls() {
        each(document.querySelectorAll('.an-range'), function (button) {
            button.addEventListener('click', function () {
                state.range = button.getAttribute('data-range');
                state.from = state.to = null;
                el.from.value = el.to.value = '';
                pushUrlState();
                load();
            });
        });

        document.getElementById('an-apply').addEventListener('click', function () {
            if (!el.from.value && !el.to.value) return;
            state.from = el.from.value || null;
            state.to = el.to.value || null;
            state.range = 'custom';
            pushUrlState();
            load();
        });

        el.bots.addEventListener('change', function () {
            state.bots = el.bots.checked;
            pushUrlState();
            load();
        });

        document.getElementById('an-refresh').addEventListener('click', load);
        document.getElementById('an-retry').addEventListener('click', load);

        each(document.querySelectorAll('.an-tab'), function (tab) {
            tab.addEventListener('click', function () {
                state.facet = tab.getAttribute('data-facet');
                each(document.querySelectorAll('.an-tab'), function (other) {
                    other.classList.toggle('active', other === tab);
                });
                if (state.report) renderFacet();
            });
        });

        // The theme toggle in site.js flips data-theme on <html>; watch for it so the chart's
        // measured colours and the map redraw against the new tokens straight away.
        new MutationObserver(function () {
            if (state.report) { renderChart(); renderMap(); }
        }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

        // The chart is drawn at CSS-pixel scale (see renderChart), so it must be redrawn on resize.
        var resizeTimer = null;
        window.addEventListener('resize', function () {
            if (resizeTimer) clearTimeout(resizeTimer);
            resizeTimer = setTimeout(function () {
                if (state.report) renderChart();
            }, 150);
        });
    }

    /** Deep-linkable state, so a particular period can be bookmarked or shared. */
    function applyUrlState() {
        var params = new URLSearchParams(window.location.search);
        var range = params.get('range');
        if (range) state.range = range;
        if (params.get('from')) { state.from = params.get('from'); el.from.value = state.from; }
        if (params.get('to')) { state.to = params.get('to'); el.to.value = state.to; }
        state.bots = params.get('bots') === '1';
        el.bots.checked = state.bots;
        markActiveRange();
    }

    function pushUrlState() {
        var params = new URLSearchParams();
        if (state.from || state.to) {
            if (state.from) params.set('from', state.from);
            if (state.to) params.set('to', state.to);
        } else {
            params.set('range', state.range);
        }
        if (state.bots) params.set('bots', '1');
        history.replaceState(null, '', window.location.pathname + '?' + params.toString());
        markActiveRange();
    }

    function markActiveRange() {
        var custom = !!(state.from || state.to);
        each(document.querySelectorAll('.an-range'), function (button) {
            button.classList.toggle('active', !custom && button.getAttribute('data-range') === state.range);
        });
    }

    // ── Load ────────────────────────────────────────────────────────────────────

    function load() {
        var params = new URLSearchParams();
        if (state.from || state.to) {
            if (state.from) params.set('from', state.from);
            if (state.to) params.set('to', state.to);
        } else {
            params.set('range', state.range);
        }
        if (state.bots) params.set('bots', '1');
        // Minutes EAST of UTC, so the server can bucket by the viewer's calendar day.
        params.set('offset', String(-new Date().getTimezoneOffset()));

        setLoading(true);
        hide(el.error);

        // Only the newest request may paint; an earlier, slower response must not overwrite it.
        var token = {};
        state.inFlight = token;

        fetch(API + '?' + params.toString(), {
            headers: { 'Accept': 'application/json' },
            credentials: 'same-origin',
        }).then(function (response) {
            if (response.status === 401 || response.status === 403) {
                throw new Error('Your admin session expired — sign in again.');
            }
            if (!response.ok) throw new Error('The server returned HTTP ' + response.status + '.');
            return response.json();
        }).then(function (report) {
            if (state.inFlight !== token) return;
            state.report = report;
            setLoading(false);
            render();
        }).catch(function (error) {
            if (state.inFlight !== token) return;
            setLoading(false);
            showError(error && error.message ? error.message : 'Unexpected error.');
        });
    }

    function setLoading(on) {
        each(document.querySelectorAll('.stats, .card'), function (card) {
            card.classList.toggle('an-loading', on);
        });
    }

    function showError(message) {
        el.errorDetail.textContent = ' ' + message;
        show(el.error);
    }

    // ── Render ──────────────────────────────────────────────────────────────────

    function render() {
        var report = state.report;
        var empty = report.totals.views === 0;
        toggle(el.empty, empty);

        renderTotals();
        renderChart();
        renderMap();
        renderList(el.countries, report.countries.map(function (row) {
            return { label: row.name, flag: row.flag, views: row.views, visitors: row.visitors };
        }), 'No location data yet.');
        renderList(el.pages, report.pages.map(function (row) {
            return { label: row.key, href: safePath(row.key), views: row.views, visitors: row.visitors };
        }), 'No pages recorded.');
        renderList(el.referrers, report.referrers.map(function (row) {
            return { label: row.key, views: row.views, visitors: row.visitors };
        }), 'All traffic was direct — no referrers.');
        renderFacet();
        renderRecent();
    }

    function renderTotals() {
        var t = state.report.totals;
        setMetric('views', formatNumber(t.views));
        setMetric('visitors', formatNumber(t.visitors));
        setMetric('countries', formatNumber(t.countries));
        setMetric('bots', formatNumber(t.botViews));
        setMetric('perVisitor', t.visitors > 0 ? (t.views / t.visitors).toFixed(1) : '—');

        var located = document.querySelector('[data-metric="located"]');
        if (located) {
            located.textContent = t.views > 0
                ? Math.round((t.locatedViews / t.views) * 100) + '% of views geolocated'
                : 'no views yet';
        }

        setDelta('views', t.views, t.previousViews);
        setDelta('visitors', t.visitors, t.previousVisitors);
    }

    function setMetric(name, text) {
        var node = document.querySelector('[data-metric="' + name + '"]');
        if (node) node.textContent = text;
    }

    /** "vs previous period" — deliberately silent when there is no prior data to compare against. */
    function setDelta(name, current, previous) {
        var node = document.querySelector('[data-delta="' + name + '"]');
        if (!node) return;
        node.textContent = '';
        if (!previous) {
            node.className = 'sub muted';
            node.textContent = previous === 0 && current > 0 ? 'no prior period to compare' : '';
            return;
        }
        var change = ((current - previous) / previous) * 100;
        var direction = change > 0.5 ? 'up' : change < -0.5 ? 'down' : 'flat';
        var arrow = direction === 'up' ? '▲' : direction === 'down' ? '▼' : '▬';
        node.className = 'sub';
        var strong = document.createElement('span');
        strong.className = 'an-delta ' + direction;
        strong.textContent = arrow + ' ' + Math.abs(change).toFixed(change >= 10 || change <= -10 ? 0 : 1) + '%';
        node.appendChild(strong);
        node.appendChild(document.createTextNode(' vs previous ' + describeSpan()));
    }

    function describeSpan() {
        var r = state.report.range;
        var hours = (new Date(r.to) - new Date(r.from)) / 36e5;
        if (hours <= 25) return 'day';
        var days = Math.round(hours / 24);
        return days + ' days';
    }

    // ── Trend chart ─────────────────────────────────────────────────────────────

    function renderChart() {
        var series = state.report.series || [];
        el.granularity.textContent = 'per ' + state.report.range.granularity;
        clear(el.chart);
        hide(el.chartTip);

        if (!series.length) {
            el.chart.appendChild(emptyNote('Nothing to plot for this period.'));
            return;
        }

        // Draw at CSS-pixel scale (1 SVG unit = 1 px) so labels and strokes are never stretched by
        // a non-uniform viewBox fit. A resize listener re-runs this with the new width.
        var W = Math.max(320, Math.round(el.chart.clientWidth || 900));
        var H = W < 620 ? 210 : 260;
        var pad = { top: 14, right: 12, bottom: 26, left: 44 };
        var innerW = W - pad.left - pad.right;
        var innerH = H - pad.top - pad.bottom;

        var steps = 4;   // gridlines above the baseline
        var peak = 0;
        series.forEach(function (point) { peak = Math.max(peak, point.views, point.visitors); });
        var top = niceCeiling(peak, steps);

        var svg = svgEl('svg', { viewBox: '0 0 ' + W + ' ' + H, width: W, height: H });

        // Gradient under the views line. The stop colours live in analytics.css so they follow the
        // theme tokens (a presentation attribute could not reference a CSS variable reliably).
        var defs = svgEl('defs');
        var gradient = svgEl('linearGradient', { id: 'an-area-fill', x1: '0', y1: '0', x2: '0', y2: '1' });
        gradient.appendChild(svgEl('stop', { class: 'an-stop-top', offset: '0%' }));
        gradient.appendChild(svgEl('stop', { class: 'an-stop-bottom', offset: '100%' }));
        defs.appendChild(gradient);
        svg.appendChild(defs);

        // Horizontal gridlines + y labels.
        for (var i = 0; i <= steps; i++) {
            var value = (top / steps) * i;
            var y = pad.top + innerH - (innerH * (i / steps));
            svg.appendChild(svgEl('line', {
                class: 'an-gridline', x1: pad.left, x2: W - pad.right, y1: y, y2: y,
            }));
            var label = svgEl('text', { class: 'an-axis', x: pad.left - 8, y: y + 3.5, 'text-anchor': 'end' });
            label.textContent = formatCompact(value);
            svg.appendChild(label);
        }

        var stepX = series.length > 1 ? innerW / (series.length - 1) : 0;
        function px(index) { return pad.left + (series.length > 1 ? index * stepX : innerW / 2); }
        function py(value) { return pad.top + innerH - (top > 0 ? (value / top) * innerH : 0); }

        var viewsPath = '', visitorsPath = '';
        series.forEach(function (point, index) {
            viewsPath += (index ? 'L' : 'M') + px(index).toFixed(1) + ' ' + py(point.views).toFixed(1);
            visitorsPath += (index ? 'L' : 'M') + px(index).toFixed(1) + ' ' + py(point.visitors).toFixed(1);
        });

        svg.appendChild(svgEl('path', {
            class: 'an-area',
            d: viewsPath + 'L' + px(series.length - 1).toFixed(1) + ' ' + (pad.top + innerH) +
               'L' + px(0).toFixed(1) + ' ' + (pad.top + innerH) + 'Z',
        }));
        svg.appendChild(svgEl('path', { class: 'an-line-views', d: viewsPath }));
        svg.appendChild(svgEl('path', { class: 'an-line-visitors', d: visitorsPath }));

        // X labels — thinned so they never collide, whatever the bucket count.
        var labelEvery = Math.max(1, Math.ceil(series.length / 8));
        series.forEach(function (point, index) {
            if (index % labelEvery !== 0 && index !== series.length - 1) return;
            var text = svgEl('text', {
                class: 'an-axis', x: px(index), y: H - 8, 'text-anchor': 'middle',
            });
            text.textContent = bucketLabel(point.start, state.report.range.granularity);
            svg.appendChild(text);
        });

        // Invisible hover columns drive the tooltip — one per bucket, full height so they are easy to hit.
        var marker = svgEl('circle', { class: 'an-marker', r: 4, cx: -99, cy: -99 });
        series.forEach(function (point, index) {
            var half = series.length > 1 ? stepX / 2 : innerW / 2;
            var hit = svgEl('rect', {
                class: 'an-hit',
                x: Math.max(pad.left, px(index) - half), y: pad.top,
                width: Math.max(2, half * 2), height: innerH,
            });
            hit.addEventListener('mouseenter', function () {
                marker.setAttribute('cx', px(index));
                marker.setAttribute('cy', py(point.views));
                showTip(point, px(index) / W, py(point.views) / H);
            });
            hit.addEventListener('mouseleave', function () {
                marker.setAttribute('cx', -99);
                hide(el.chartTip);
            });
            svg.appendChild(hit);
        });
        svg.appendChild(marker);

        el.chart.appendChild(svg);
    }

    function showTip(point, fx, fy) {
        clear(el.chartTip);
        var title = document.createElement('b');
        title.textContent = bucketTitle(point.start, state.report.range.granularity);
        el.chartTip.appendChild(title);
        el.chartTip.appendChild(tipRow('Page views', point.views));
        el.chartTip.appendChild(tipRow('Visitors', point.visitors));

        var box = el.chart.getBoundingClientRect();
        el.chartTip.style.left = Math.round(fx * box.width) + 'px';
        el.chartTip.style.top = Math.max(0, Math.round(fy * box.height) - 12) + 'px';
        show(el.chartTip);
    }

    function tipRow(label, value) {
        var row = document.createElement('div');
        row.className = 'an-tip-row';
        var name = document.createElement('i');
        name.textContent = label;
        var amount = document.createElement('span');
        amount.textContent = formatNumber(value);
        row.appendChild(name);
        row.appendChild(amount);
        return row;
    }

    // ── Map ─────────────────────────────────────────────────────────────────────

    function lat2y(lat) { return (90 - lat) * (MAP_H / 180); }
    function lon2x(lon) { return (lon + 180) * (MAP_W / 360); }

    function renderMap() {
        var points = state.report.map || [];
        clear(el.map);

        var approx = points.filter(function (p) { return p.approximate; }).length;
        el.mapNote.textContent = points.length
            ? points.length + ' location' + (points.length === 1 ? '' : 's') +
              (approx ? ' · ' + approx + ' country-level' : '')
            : '';

        if (!points.length) {
            el.map.appendChild(emptyNote(
                'No location data in this period. Requests that do not come through Cloudflare — ' +
                'local development, direct hits, health checks — have no country and are not plotted.'));
            return;
        }

        var svg = svgEl('svg', {
            viewBox: [
                -MAP_MARGIN,
                (MAP_VIEW.top - MAP_MARGIN).toFixed(1),
                MAP_W + MAP_MARGIN * 2,
                (MAP_VIEW.bottom - MAP_VIEW.top + MAP_MARGIN * 2).toFixed(1),
            ].join(' '),
            role: 'img', 'aria-label': 'Map of visitor locations',
        });

        if (window.V2EN_WORLD_OUTLINE) {
            svg.appendChild(svgEl('path', {
                class: 'an-land', d: window.V2EN_WORLD_OUTLINE, 'fill-rule': 'evenodd',
            }));
        }

        // Area ∝ views (radius ∝ √views) so a busy country does not swallow the map.
        var peak = points.reduce(function (max, p) { return Math.max(max, p.views); }, 1);
        var minR = 2.6;

        points.slice().sort(function (a, b) { return b.views - a.views; }).forEach(function (point) {
            var r = minR + (MAP_MAX_R - minR) * Math.sqrt(point.views / peak);
            var circle = svgEl('circle', {
                class: 'an-point' + (point.approximate ? ' approx' : ''),
                cx: lon2x(point.longitude).toFixed(1),
                cy: lat2y(point.latitude).toFixed(1),
                r: r.toFixed(1),
            });
            var title = svgEl('title');
            title.textContent = point.label +
                ' — ' + formatNumber(point.views) + ' view' + (point.views === 1 ? '' : 's') +
                ', ' + formatNumber(point.visitors) + ' visitor' + (point.visitors === 1 ? '' : 's') +
                (point.approximate ? ' (country-level)' : '');
            circle.appendChild(title);
            svg.appendChild(circle);
        });

        el.map.appendChild(svg);
    }

    // ── Breakdown lists ─────────────────────────────────────────────────────────

    function renderFacet() {
        var rows = state.report[state.facet] || [];
        renderList(el.devices, rows.map(function (row) {
            return { label: prettyDevice(row.key), views: row.views, visitors: row.visitors };
        }), 'No client information recorded.');
    }

    function prettyDevice(key) {
        if (key === 'desktop') return 'Desktop';
        if (key === 'mobile') return 'Mobile';
        if (key === 'tablet') return 'Tablet';
        if (key === 'bot') return 'Bot';
        if (key === 'unknown') return 'Unknown';
        return key;
    }

    function renderList(container, rows, emptyText) {
        clear(container);
        if (!rows.length) {
            container.appendChild(emptyNote(emptyText));
            return;
        }
        var peak = rows.reduce(function (max, row) { return Math.max(max, row.views); }, 1);

        rows.forEach(function (row) {
            var line = document.createElement('div');
            line.className = 'an-row';

            var bar = document.createElement('span');
            bar.className = 'an-bar';
            bar.style.width = Math.max(2, (row.views / peak) * 100) + '%';
            line.appendChild(bar);

            if (row.flag) {
                var flag = document.createElement('span');
                flag.className = 'an-flag';
                flag.textContent = row.flag;
                line.appendChild(flag);
            }

            var label = document.createElement(row.href ? 'a' : 'span');
            label.className = 'an-label';
            label.textContent = row.label;
            label.title = row.label;
            if (row.href) {
                label.href = row.href;
                label.target = '_blank';
                label.rel = 'noreferrer';
            }
            line.appendChild(label);

            var visitors = document.createElement('span');
            visitors.className = 'an-sub';
            visitors.textContent = formatNumber(row.visitors) + ' vis.';
            line.appendChild(visitors);

            var count = document.createElement('span');
            count.className = 'an-count';
            count.textContent = formatNumber(row.views);
            line.appendChild(count);

            container.appendChild(line);
        });
    }

    // ── Recent visits ───────────────────────────────────────────────────────────

    function renderRecent() {
        var rows = state.report.recent || [];
        clear(el.recent);

        if (!rows.length) {
            var tr = document.createElement('tr');
            var td = document.createElement('td');
            td.colSpan = 7;
            td.appendChild(emptyNote('No visits recorded in this period.'));
            tr.appendChild(td);
            el.recent.appendChild(tr);
            return;
        }

        rows.forEach(function (visit) {
            var tr = document.createElement('tr');

            var when = document.createElement('td');
            when.className = 'nowrap';
            var time = document.createElement('time');
            time.dateTime = visit.utc;
            time.textContent = formatLocal(visit.utc);
            when.appendChild(time);
            tr.appendChild(when);

            var page = document.createElement('td');
            var href = safePath(visit.path);
            var link = document.createElement(href ? 'a' : 'span');
            link.textContent = visit.path;
            if (href) {
                link.href = href;
                link.target = '_blank';
                link.rel = 'noreferrer';
            }
            page.appendChild(link);
            tr.appendChild(page);

            var place = document.createElement('td');
            place.className = 'nowrap';
            place.textContent = (visit.flag ? visit.flag + ' ' : '') + describePlace(visit);
            tr.appendChild(place);

            var client = document.createElement('td');
            client.className = 'nowrap';
            client.textContent = [visit.browser, visit.os].filter(Boolean).join(' · ') || '—';
            if (visit.isBot) {
                var badge = document.createElement('span');
                badge.className = 'badge info an-bot';
                badge.textContent = 'bot';
                client.appendChild(badge);
            }
            tr.appendChild(client);

            var referrer = document.createElement('td');
            referrer.textContent = visit.referrerHost || 'direct';
            if (!visit.referrerHost) referrer.className = 'muted';
            tr.appendChild(referrer);

            var visitor = document.createElement('td');
            visitor.className = 'nowrap';
            var hash = document.createElement('span');
            hash.className = 'an-visitor';
            hash.title = 'Daily one-way hash — not an IP address, and different tomorrow for the same person.';
            hash.textContent = visit.visitor || '—';
            visitor.appendChild(hash);
            tr.appendChild(visitor);

            var status = document.createElement('td');
            status.className = 'nowrap';
            var code = document.createElement('span');
            code.className = 'badge ' + (visit.status >= 500 ? 'err' : visit.status >= 400 ? 'warn' : 'ok');
            code.textContent = String(visit.status);
            status.appendChild(code);
            tr.appendChild(status);

            el.recent.appendChild(tr);
        });
    }

    function describePlace(visit) {
        if (visit.city && visit.countryName !== 'Unknown') return visit.city + ', ' + visit.countryName;
        if (visit.city) return visit.city;
        if (visit.countryName && visit.countryName !== 'Unknown') return visit.countryName;
        return 'Unknown';
    }

    // ── Formatting helpers ──────────────────────────────────────────────────────

    /**
     * A recorded path is whatever a client asked for, so it is only turned into a link when it is
     * unambiguously same-origin. "//example.com" is a legal request path but a protocol-relative URL
     * in an href, which would take the admin off-site — such rows render as plain text instead.
     */
    function safePath(path) {
        if (typeof path !== 'string') return null;
        if (path.charAt(0) !== '/' || path.charAt(1) === '/' || path.charAt(1) === '\\') return null;
        return path;
    }

    function formatNumber(value) {
        return (value || 0).toLocaleString();
    }

    function formatCompact(value) {
        if (value >= 1000000) return (value / 1000000).toFixed(value % 1000000 ? 1 : 0) + 'M';
        if (value >= 1000) return (value / 1000).toFixed(value % 1000 ? 1 : 0) + 'k';
        return String(Math.round(value));
    }

    /**
     * Pick the y-axis maximum: the smallest "round" per-gridline step that still clears the peak.
     * Choosing the STEP (rather than the top) is what keeps every gridline label a whole number —
     * a top of 10 across 4 gridlines would otherwise print 2.5 / 5 / 7.5.
     */
    function niceCeiling(peak, steps) {
        if (peak <= steps) return steps;                       // tiny counts: one per gridline
        var needed = peak / steps;
        var magnitude = Math.pow(10, Math.floor(Math.log10(needed)));
        var ladder = [1, 2, 3, 4, 5, 6, 8, 10];
        for (var i = 0; i < ladder.length; i++) {
            var step = ladder[i] * magnitude;
            if (step >= needed) return step * steps;
        }
        return needed * steps;
    }

    function bucketLabel(iso, granularity) {
        var date = new Date(iso);
        if (granularity === 'hour') {
            return date.toLocaleTimeString([], { hour: 'numeric' });
        }
        return date.toLocaleDateString([], { month: 'short', day: 'numeric' });
    }

    function bucketTitle(iso, granularity) {
        var date = new Date(iso);
        if (granularity === 'hour') {
            return date.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric' });
        }
        if (granularity === 'week') {
            return 'Week of ' + date.toLocaleDateString([], { month: 'short', day: 'numeric' });
        }
        return date.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
    }

    function formatLocal(iso) {
        var date = new Date(iso);
        return date.toLocaleString([], {
            month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
        });
    }

    // ── Tiny DOM helpers ────────────────────────────────────────────────────────

    function svgEl(name, attributes) {
        var node = document.createElementNS(SVG_NS, name);
        for (var key in attributes || {}) {
            if (Object.prototype.hasOwnProperty.call(attributes, key)) {
                node.setAttribute(key, attributes[key]);
            }
        }
        return node;
    }

    function emptyNote(text) {
        var p = document.createElement('p');
        p.className = 'empty';
        p.textContent = text;
        return p;
    }

    function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }
    function show(node) { node.hidden = false; }
    function hide(node) { node.hidden = true; }
    function toggle(node, on) { node.hidden = !on; }
    function each(list, fn) { Array.prototype.forEach.call(list, fn); }
})();
