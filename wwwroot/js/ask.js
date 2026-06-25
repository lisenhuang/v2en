(function () {
    "use strict";

    var form = document.getElementById("ask-form");
    if (!form) return;

    var input = document.getElementById("ask-input");
    var keyEl = document.getElementById("ask-key");
    var keyWrap = document.getElementById("ask-key-wrap");
    var keyPill = document.getElementById("ask-key-pill");
    var remember = document.getElementById("ask-remember");
    var windowEl = document.getElementById("ask-window");
    var thread = document.getElementById("ask-thread");
    var empty = document.getElementById("ask-empty");
    var sendBtn = document.getElementById("ask-send");
    var KEY_LS = "v2en_gemini_key";

    function collapseKey() { keyWrap.hidden = true; keyPill.hidden = false; }
    function expandKey() { keyWrap.hidden = false; keyPill.hidden = true; keyEl.focus(); }
    keyPill.addEventListener("click", expandKey);

    // Restore a remembered key — then collapse the bar so the chat takes the space.
    try {
        var saved = localStorage.getItem(KEY_LS);
        if (saved) { keyEl.value = saved; remember.checked = true; collapseKey(); }
    } catch (e) { }

    function persistKey() {
        try {
            if (remember.checked && keyEl.value.trim()) localStorage.setItem(KEY_LS, keyEl.value.trim());
            else localStorage.removeItem(KEY_LS);
        } catch (e) { }
    }
    remember.addEventListener("change", persistKey);
    keyEl.addEventListener("change", persistKey);

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
                html += "<li><a href=\"/t/" + encodeURIComponent(s.id) + "\">" + esc(s.title) + "</a>"
                    + " <a class=\"ask-src-ext\" href=\"" + esc(s.url) + "\" target=\"_blank\" rel=\"noopener\">v2ex ↗</a>"
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
        if (!key) { expandKey(); flash(keyEl); return; }
        persistKey();
        if (remember.checked) collapseKey();

        addUser(q);
        input.value = ""; autogrow();
        var botEl = addAssistant();
        sendBtn.disabled = true;

        fetch("/api/chat", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ question: q, apiKey: key, window: windowEl.value })
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
