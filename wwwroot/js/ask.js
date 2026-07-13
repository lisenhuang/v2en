(function () {
    "use strict";

    var form = document.getElementById("ask-form");
    if (!form) return;

    var input = document.getElementById("ask-input");
    var keyEl = document.getElementById("ask-key");
    var remember = document.getElementById("ask-remember");
    var windowEl = document.getElementById("ask-window");
    var modelEl = document.getElementById("ask-model");
    var settingsBtn = document.getElementById("ask-settings-btn");
    var settingsPanel = document.getElementById("ask-settings");
    var keyDot = document.getElementById("ask-key-dot");
    var thread = document.getElementById("ask-thread");
    var empty = document.getElementById("ask-empty");
    var sendBtn = document.getElementById("ask-send");
    var KEY_LS = "v2en_gemini_key";
    var WIN_LS = "v2en_ask_window";
    var MODEL_LS = "v2en_gemini_model";

    // In-memory transcript of this chat so follow-ups ("is there more?") keep context. Each entry is
    // { role: "user" | "model", text }. A pair is pushed only after an answer succeeds, so error
    // bubbles never poison the context. Capped to the most recent turns to bound the request size.
    var history = [];
    var MAX_HISTORY = 12; // ~6 Q&A pairs

    // ── Settings panel (key + model) behind the gear button ──────────────────────────────
    function setSettings(open) {
        settingsPanel.hidden = !open;
        settingsBtn.setAttribute("aria-expanded", open ? "true" : "false");
        settingsBtn.classList.toggle("is-open", open);
        if (open) keyEl.focus();
    }
    settingsBtn.addEventListener("click", function () { setSettings(settingsPanel.hidden); });

    function reflectKey() {
        var has = !!keyEl.value.trim();
        keyDot.classList.toggle("is-set", has);
        keyDot.title = has ? "Key set" : "No key set";
    }

    // Restore a remembered key. If none, open settings so first-time visitors see where to add it.
    var hadSavedKey = false;
    try {
        var saved = localStorage.getItem(KEY_LS);
        if (saved) { keyEl.value = saved; remember.checked = true; hadSavedKey = true; }
    } catch (e) { }
    reflectKey();
    setSettings(!hadSavedKey);

    function persistKey() {
        try {
            if (remember.checked && keyEl.value.trim()) localStorage.setItem(KEY_LS, keyEl.value.trim());
            else localStorage.removeItem(KEY_LS);
        } catch (e) { }
    }
    remember.addEventListener("change", persistKey);
    keyEl.addEventListener("input", reflectKey);

    // ── Time-window choice, remembered across reloads ────────────────────────────────────
    try {
        var savedWin = localStorage.getItem(WIN_LS);
        if (savedWin) windowEl.value = savedWin;
    } catch (e) { }
    windowEl.addEventListener("change", function () {
        try { localStorage.setItem(WIN_LS, windowEl.value); } catch (e) { }
    });

    // ── Chat model picker: load the visitor's own generation models from their key (never hardcoded) ──
    var savedModel = "";
    try { savedModel = localStorage.getItem(MODEL_LS) || ""; } catch (e) { }
    var modelsLoadedFor = null;

    function persistModel() { try { localStorage.setItem(MODEL_LS, modelEl.value); } catch (e) { } }
    modelEl.addEventListener("change", persistModel);

    // When the visitor hasn't explicitly picked a model, default to the latest Flash-Lite
    // (cheapest / fastest tier). Returns "" if the key exposes no Flash-Lite model, which keeps
    // the existing "Default model" (server decides) behavior. No model id is hardcoded.
    function pickDefaultModel(models) {
        function ver(m) {
            var match = (m.id + " " + (m.displayName || "")).match(/(\d+(?:\.\d+)?)/);
            return match ? parseFloat(match[1]) : 0;
        }
        var lite = models.filter(function (m) {
            var s = (m.id + " " + (m.displayName || "")).toLowerCase().replace(/\s+/g, "");
            return s.indexOf("flash-lite") >= 0 || s.indexOf("flashlite") >= 0;
        });
        if (!lite.length) return "";
        // Prefer an explicit rolling "latest" alias; otherwise the highest version number.
        var alias = lite.filter(function (m) { return /latest/i.test(m.id); });
        var pool = alias.length ? alias : lite;
        return pool.reduce(function (best, m) { return ver(m) > ver(best) ? m : best; }, pool[0]).id;
    }

    function setModelOptions(models) {
        var want = modelEl.value || savedModel;
        modelEl.innerHTML = "";
        var def = document.createElement("option");
        def.value = ""; def.textContent = "Default model";
        modelEl.appendChild(def);
        models.forEach(function (m) {
            var o = document.createElement("option");
            o.value = m.id; o.textContent = m.displayName || m.id;
            modelEl.appendChild(o);
        });
        if (want && models.some(function (m) { return m.id === want; })) modelEl.value = want;
        else modelEl.value = pickDefaultModel(models);   // no explicit choice → latest Flash-Lite
        modelEl.disabled = false;
        modelEl.title = "Chat model";
    }

    function loadModels() {
        var key = keyEl.value.trim();
        if (!key || key === modelsLoadedFor) return;
        fetch("/api/chat/models", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ apiKey: key })
        }).then(function (r) {
            return r.ok ? r.json() : null;
        }).then(function (data) {
            if (data && data.models && data.models.length) {
                modelsLoadedFor = key;
                setModelOptions(data.models);
            }
        }).catch(function () { });
    }

    // Persist the key, refresh the dot, AND (re)load that key's models whenever it changes.
    keyEl.addEventListener("change", function () { persistKey(); reflectKey(); loadModels(); });
    if (keyEl.value.trim()) loadModels();

    // ── Keep the composer pinned right on top of the on-screen keyboard (mobile) ──────────
    // The chat is a position:fixed overlay (see site.css). iOS Safari doesn't shrink the
    // layout viewport when the keyboard opens — it scrolls the page up to reveal the focused
    // field, which would shove a flow-positioned composer off the top of the screen (the bug
    // this fixes). window.visualViewport tells us the actually-visible region above the
    // keyboard; we re-anchor the overlay to it on every resize/scroll so the composer always
    // rides on top of the keyboard. (Chrome resizes content natively via the viewport's
    // interactive-widget=resizes-content, and stays consistent with the same math.)
    var HEADER_H = 61; // matches the .site-header height reserved in site.css
    var vv = window.visualViewport;
    if (vv) {
        var root = document.documentElement;
        var syncViewport = function () {
            // Visible region in layout coords is [offsetTop, offsetTop + height]. Keep the
            // header visible while any of it is on screen, otherwise butt the chat against
            // the top of the visible area (e.g. once the page has scrolled the header away).
            var top = Math.max(HEADER_H, vv.offsetTop);
            var height = (vv.offsetTop + vv.height) - top;
            root.style.setProperty("--ask-top", Math.round(top) + "px");
            root.style.setProperty("--ask-h", Math.max(0, Math.round(height)) + "px");
        };
        syncViewport();
        vv.addEventListener("resize", syncViewport);
        vv.addEventListener("scroll", syncViewport);
    }

    // ── Composer ─────────────────────────────────────────────────────────────────────────
    document.querySelectorAll(".ask-chip").forEach(function (c) {
        c.addEventListener("click", function () {
            input.value = c.getAttribute("data-q");
            input.focus(); autogrow();
        });
    });

    function autogrow() {
        input.style.height = "auto";
        input.style.height = Math.min(160, input.scrollHeight) + "px";
    }
    input.addEventListener("input", autogrow);
    input.addEventListener("keydown", function (e) {
        if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); }
    });
    // Once the keyboard has animated in and the chat resized, keep the latest reply visible.
    input.addEventListener("focus", function () { setTimeout(scrollDown, 300); });

    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }
    function hideEmpty() { if (empty) empty.style.display = "none"; }
    function scrollDown() { thread.scrollTop = thread.scrollHeight; }

    // ── Minimal, safe Markdown → HTML for answers ────────────────────────────────────────
    // Gemini replies in Markdown (**bold**, "*" bullets, `code`, …) which used to be shown
    // as raw text. Everything is HTML-escaped FIRST, then a small whitelist of Markdown is
    // turned into tags, so model output can never inject markup. Links are only linkified
    // for http(s) and site-relative URLs.
    function mdInline(s) {
        // s is already HTML-escaped by the caller
        s = s.replace(/`([^`]+)`/g, "<code>$1</code>");
        // no quotes in the URL: esc() leaves " untouched, so this keeps hrefs unbreakable
        s = s.replace(/\[([^\]]+)\]\(([^()\s"']+)\)/g, function (all, text, url) {
            if (!/^(https?:\/\/|\/)/i.test(url)) return all;
            return '<a href="' + url + '" target="_blank" rel="noopener">' + text + "</a>";
        });
        s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
        s = s.replace(/__([^_]+)__/g, "<strong>$1</strong>");
        s = s.replace(/(^|[\s(])\*([^*\s][^*]*)\*/g, "$1<em>$2</em>");
        s = s.replace(/(^|[\s(])_([^_\s][^_]*)_(?=[\s.,;:!?)]|$)/g, "$1<em>$2</em>");
        return s;
    }
    function mdToHtml(md) {
        var lines = esc(md).split(/\r?\n/);
        var out = [], para = [], i = 0, m;
        function flush() {
            if (para.length) { out.push("<p>" + para.map(mdInline).join("<br>") + "</p>"); para = []; }
        }
        while (i < lines.length) {
            var line = lines[i];
            if (/^\s*```/.test(line)) {                                  // fenced code block
                flush();
                var code = [];
                for (i++; i < lines.length && !/^\s*```/.test(lines[i]); i++) code.push(lines[i]);
                i++;                                                     // skip the closing fence
                out.push("<pre><code>" + code.join("\n") + "</code></pre>");
                continue;
            }
            if ((m = line.match(/^\s{0,3}(#{1,6})\s+(.+)$/))) {          // heading → h3…h6 in the bubble
                flush();
                var lvl = Math.min(6, m[1].length + 2);
                out.push("<h" + lvl + ">" + mdInline(m[2]) + "</h" + lvl + ">");
                i++; continue;
            }
            if (/^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$/.test(line)) { flush(); out.push("<hr>"); i++; continue; }
            if (/^\s*[*+-]\s+/.test(line)) {                             // bullet list
                flush();
                var ul = [];
                while (i < lines.length && (m = lines[i].match(/^\s*[*+-]\s+(.*)$/))) { ul.push(m[1]); i++; }
                out.push("<ul>" + ul.map(function (t) { return "<li>" + mdInline(t) + "</li>"; }).join("") + "</ul>");
                continue;
            }
            if (/^\s*\d+[.)]\s+/.test(line)) {                           // numbered list
                flush();
                var ol = [];
                while (i < lines.length && (m = lines[i].match(/^\s*\d+[.)]\s+(.*)$/))) { ol.push(m[1]); i++; }
                out.push("<ol>" + ol.map(function (t) { return "<li>" + mdInline(t) + "</li>"; }).join("") + "</ol>");
                continue;
            }
            if (/^\s*&gt;\s?/.test(line)) {                              // blockquote (">" is escaped to &gt;)
                flush();
                var q = [];
                while (i < lines.length && (m = lines[i].match(/^\s*&gt;\s?(.*)$/))) { q.push(m[1]); i++; }
                out.push("<blockquote>" + q.map(mdInline).join("<br>") + "</blockquote>");
                continue;
            }
            if (/^\s*$/.test(line)) { flush(); i++; continue; }
            para.push(line); i++;
        }
        flush();
        return out.join("");
    }

    function addUser(text) {
        hideEmpty();
        var el = document.createElement("div");
        el.className = "ask-msg ask-user";
        el.innerHTML = '<div class="ask-bubble">' + esc(text) + "</div>";
        thread.appendChild(el); scrollDown();
    }
    function addAssistant() {
        var el = document.createElement("div");
        el.className = "ask-msg ask-bot";
        el.innerHTML = '<div class="ask-bubble"><span class="ask-typing"><i></i><i></i><i></i></span></div>';
        thread.appendChild(el); scrollDown();
        return el;
    }
    function renderAnswer(el, answer, sources) {
        var html = '<div class="ask-bubble"><div class="ask-answer">' + mdToHtml(answer) + "</div>";
        if (sources && sources.length) {
            html += '<div class="ask-sources"><span class="ask-sources-h">Sources</span><ul>';
            sources.forEach(function (s) {
                var d = new Date(s.published);
                // Link only to our own mirror page (/t/{id}); the detail page itself links out to
                // v2ex, so we never surface a v2ex.com URL here.
                html += "<li><a href=\"/t/" + encodeURIComponent(s.id) + "\" target=\"_blank\" rel=\"noopener\">" + esc(s.title) + "</a>"
                    + " <time datetime=\"" + esc(s.published) + "\">" + (isNaN(d) ? "" : d.toLocaleDateString()) + "</time></li>";
            });
            html += "</ul></div>";
        }
        html += "</div>";
        el.innerHTML = html;
        el.querySelectorAll("time[datetime]").forEach(function (t) {
            var dd = new Date(t.getAttribute("datetime"));
            if (!isNaN(dd)) t.title = dd.toLocaleString();
        });
        scrollDown();
    }
    // Inline alert icon (mirrors Utilities/Icons.cs "alert") used in error bubbles.
    var ALERT_SVG = '<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>';
    function renderError(el, msg) {
        el.classList.add("ask-error");
        el.innerHTML = '<div class="ask-bubble">' + ALERT_SVG + " " + esc(msg) + "</div>";
        scrollDown();
    }
    function flash(el) { el.classList.add("ask-flash"); setTimeout(function () { el.classList.remove("ask-flash"); }, 1200); }

    form.addEventListener("submit", function (e) {
        e.preventDefault();
        var q = input.value.trim();
        if (!q) return;
        var key = keyEl.value.trim();
        if (!key) { setSettings(true); flash(keyEl); return; }
        persistKey();

        addUser(q);
        input.value = ""; autogrow();
        var botEl = addAssistant();
        sendBtn.disabled = true;

        // Snapshot the prior turns to send; the current question is sent separately as `question`.
        var priorHistory = history.slice(-MAX_HISTORY);

        fetch("/api/chat", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ question: q, apiKey: key, window: windowEl.value, model: modelEl.value || null, history: priorHistory })
        }).then(function (r) {
            return r.json().catch(function () { return {}; }).then(function (data) {
                return { ok: r.ok, status: r.status, data: data };
            });
        }).then(function (res) {
            if (res.ok && res.data && res.data.answer != null) {
                renderAnswer(botEl, res.data.answer, res.data.sources || []);
                // Only remember successful exchanges so error bubbles never become context.
                history.push({ role: "user", text: q });
                history.push({ role: "model", text: res.data.answer });
                if (history.length > MAX_HISTORY) history = history.slice(-MAX_HISTORY);
            } else {
                renderError(botEl, (res.data && res.data.error) || "Something went wrong. Try again.");
            }
        }).catch(function () {
            renderError(botEl, "Couldn't reach the server. Check your connection.");
        }).finally(function () {
            sendBtn.disabled = false; input.focus();
        });
    });

    autogrow();
})();
