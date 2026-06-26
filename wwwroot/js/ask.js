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

    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }
    function hideEmpty() { if (empty) empty.style.display = "none"; }
    function scrollDown() { thread.scrollTop = thread.scrollHeight; }

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
        var html = '<div class="ask-bubble"><div class="ask-answer">' + esc(answer) + "</div>";
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
    function renderError(el, msg) {
        el.classList.add("ask-error");
        el.innerHTML = '<div class="ask-bubble">⚠ ' + esc(msg) + "</div>";
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

        fetch("/api/chat", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ question: q, apiKey: key, window: windowEl.value, model: modelEl.value || null })
        }).then(function (r) {
            return r.json().catch(function () { return {}; }).then(function (data) {
                return { ok: r.ok, status: r.status, data: data };
            });
        }).then(function (res) {
            if (res.ok && res.data && res.data.answer != null) {
                renderAnswer(botEl, res.data.answer, res.data.sources || []);
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
