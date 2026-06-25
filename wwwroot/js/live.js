(function () {
    "use strict";

    var micBtn = document.getElementById("live-mic");
    if (!micBtn) return;

    var keyEl = document.getElementById("ask-key");
    var remember = document.getElementById("ask-remember");
    var modelEl = document.getElementById("live-model");
    var settingsBtn = document.getElementById("ask-settings-btn");
    var settingsPanel = document.getElementById("ask-settings");
    var keyDot = document.getElementById("ask-key-dot");
    var statusEl = document.getElementById("live-status");
    var captionEl = document.getElementById("live-caption");
    var logEl = document.getElementById("live-log");

    var KEY_LS = "v2en_gemini_key";              // shared with the /ask page
    var MODEL_LS = "v2en_gemini_live_model";

    // Gemini Live API (v1beta bidirectional streaming). The browser connects directly with the
    // visitor's own key — audio never passes through this server.
    var WS_BASE = "wss://generativelanguage.googleapis.com/ws/" +
        "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    var INPUT_RATE = 16000;   // Gemini expects 16 kHz PCM16 mono input
    var OUTPUT_RATE = 24000;  // Gemini streams 24 kHz PCM16 mono output

    // Grounding: the model answers about the V2EX feed by calling a search tool (function calling),
    // which retrieves real posts from the server — the same RAG the text chat uses.
    var GROUND_PROMPT =
        "You are a friendly voice assistant for an English-language mirror of the Chinese tech forum " +
        "V2EX. Whenever the user asks about posts, discussions, news, opinions, or topics, you MUST call " +
        "the search_v2ex_posts function first and base your answer ONLY on the posts it returns, citing " +
        "them by title. If it returns no posts, say you couldn't find any in the feed. For small talk you " +
        "may answer directly. Keep replies concise and conversational, and always speak in English.";
    var SEARCH_DECL = {
        name: "search_v2ex_posts",
        description: "Search the English V2EX post feed for posts relevant to the user's question. " +
            "Call this before answering anything about posts, discussions, or topics on the feed.",
        parameters: {
            type: "object",
            properties: { query: { type: "string", description: "The search query, in English." } },
            required: ["query"]
        }
    };

    // ── Settings panel (key + model) ─────────────────────────────────────────────────────
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

    // ── Live model picker (loaded from the visitor's own key; never hardcoded) ────────────
    var savedModel = "";
    try { savedModel = localStorage.getItem(MODEL_LS) || ""; } catch (e) { }
    var modelsLoadedFor = null;

    modelEl.addEventListener("change", function () {
        try { localStorage.setItem(MODEL_LS, modelEl.value); } catch (e) { }
    });

    function setModelOptions(models) {
        var want = modelEl.value || savedModel;
        modelEl.innerHTML = "";
        if (!models.length) {
            var none = document.createElement("option");
            none.value = ""; none.textContent = "No live models on this key";
            modelEl.appendChild(none);
            modelEl.disabled = true;
            return;
        }
        models.forEach(function (m) {
            var o = document.createElement("option");
            o.value = m.id; o.textContent = m.displayName || m.id;
            modelEl.appendChild(o);
        });
        if (want && models.some(function (m) { return m.id === want; })) modelEl.value = want;
        modelEl.disabled = false;
        modelEl.title = "Live audio model";
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
            if (data && data.live) {
                modelsLoadedFor = key;
                setModelOptions(data.live);
            }
        }).catch(function () { });
    }
    keyEl.addEventListener("change", function () { persistKey(); reflectKey(); loadModels(); });
    if (keyEl.value.trim()) loadModels();

    // ── UI helpers ────────────────────────────────────────────────────────────────────────
    function setStatus(state, text) {
        statusEl.setAttribute("data-state", state);
        statusEl.textContent = text;
    }
    function caption(text) { captionEl.textContent = text; }
    function logLine(role, text) {
        if (!text) return;
        var last = logEl.lastElementChild;
        // Append to the same line while a transcript streams in token-by-token.
        if (last && last.getAttribute("data-role") === role && last.getAttribute("data-open") === "1") {
            last.querySelector(".live-log-text").textContent += text;
        } else {
            if (last) last.setAttribute("data-open", "0");
            var el = document.createElement("div");
            el.className = "live-log-line live-" + role;
            el.setAttribute("data-role", role);
            el.setAttribute("data-open", "1");
            el.innerHTML = '<span class="live-log-who">' + (role === "you" ? "You" : "Gemini") + '</span>'
                + '<span class="live-log-text"></span>';
            el.querySelector(".live-log-text").textContent = text;
            logEl.appendChild(el);
        }
        logEl.scrollTop = logEl.scrollHeight;
    }
    function closeLogLines() {
        var last = logEl.lastElementChild;
        if (last) last.setAttribute("data-open", "0");
    }

    // ── base64 <-> PCM helpers ──────────────────────────────────────────────────────────────
    function b64ToInt16(b64) {
        var bin = atob(b64);
        var len = bin.length;
        var bytes = new Uint8Array(len);
        for (var i = 0; i < len; i++) bytes[i] = bin.charCodeAt(i);
        // PCM16 little-endian; view the byte buffer as Int16 (handle odd lengths defensively).
        return new Int16Array(bytes.buffer, 0, len >> 1);
    }
    function int16ToB64(int16) {
        var bytes = new Uint8Array(int16.buffer, int16.byteOffset, int16.byteLength);
        var out = "";
        var CHUNK = 0x8000;
        for (var i = 0; i < bytes.length; i += CHUNK) {
            out += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK));
        }
        return btoa(out);
    }

    // ── Session state ─────────────────────────────────────────────────────────────────────
    var ws = null, micStream = null, inCtx = null, outCtx = null, procNode = null, srcNode = null;
    var running = false, playHead = 0, queued = [];

    function stopPlayback() {
        queued.forEach(function (s) { try { s.stop(); } catch (e) { } });
        queued = [];
        playHead = 0;
    }

    function playPcm(int16) {
        if (!outCtx) return;
        var f32 = new Float32Array(int16.length);
        for (var i = 0; i < int16.length; i++) f32[i] = int16[i] / 32768;
        var buf = outCtx.createBuffer(1, f32.length, OUTPUT_RATE);
        buf.getChannelData(0).set(f32);
        var src = outCtx.createBufferSource();
        src.buffer = buf;
        src.connect(outCtx.destination);
        var now = outCtx.currentTime;
        if (playHead < now) playHead = now;
        src.start(playHead);
        playHead += buf.duration;
        queued.push(src);
        src.onended = function () {
            var k = queued.indexOf(src);
            if (k >= 0) queued.splice(k, 1);
            if (running && !queued.length) { setStatus("listening", "Listening"); caption("Listening… ask about the feed"); }
        };
        setStatus("speaking", "Speaking");
    }

    function sendToolResponse(fc, posts) {
        var result = posts && posts.length
            ? posts.map(function (p, i) {
                return "[#" + (i + 1) + "] " + p.title + "\n" + (p.url || "") + "\n" + (p.snippet || "");
            }).join("\n\n")
            : "No matching posts were found in the feed.";
        try {
            ws.send(JSON.stringify({
                toolResponse: { functionResponses: [{ id: fc.id, name: fc.name, response: { result: result } }] }
            }));
        } catch (e) { }
    }

    function handleToolCall(toolCall) {
        var calls = (toolCall && toolCall.functionCalls) || [];
        calls.forEach(function (fc) {
            if (fc.name !== "search_v2ex_posts") { sendToolResponse(fc, []); return; }
            var query = (fc.args && fc.args.query) || "";
            setStatus("searching", "Searching");
            caption("Searching the feed…");
            fetch("/api/live/search", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ question: query, apiKey: keyEl.value.trim(), window: "all" })
            }).then(function (r) {
                return r.ok ? r.json() : { posts: [] };
            }).then(function (data) {
                if (query) logLine("you", query);
                closeLogLines();
                sendToolResponse(fc, (data && data.posts) || []);
            }).catch(function () { sendToolResponse(fc, []); });
        });
    }

    function handleServerMessage(obj) {
        if (window.LIVE_DEBUG) console.debug("[live] <<", obj);
        // Defensive: if Gemini ever sends a JSON error frame instead of closing, surface it.
        if (obj.error) {
            fail("Gemini error: " + (obj.error.message || JSON.stringify(obj.error)), obj.error);
            return;
        }
        if (obj.setupComplete) {
            setStatus("listening", "Listening");
            caption("Listening… ask about the feed");
            return;
        }
        if (obj.toolCall) { handleToolCall(obj.toolCall); return; }
        if (obj.toolCallCancellation) return;
        var sc = obj.serverContent;
        if (!sc) return;
        if (sc.interrupted) { stopPlayback(); setStatus("listening", "Listening"); return; }
        if (sc.inputTranscription && sc.inputTranscription.text) logLine("you", sc.inputTranscription.text);
        if (sc.outputTranscription && sc.outputTranscription.text) logLine("ai", sc.outputTranscription.text);
        if (sc.modelTurn && sc.modelTurn.parts) {
            sc.modelTurn.parts.forEach(function (p) {
                if (p.inlineData && p.inlineData.data &&
                    /audio\/pcm/i.test(p.inlineData.mimeType || "audio/pcm")) {
                    playPcm(b64ToInt16(p.inlineData.data));
                } else if (p.text) {
                    logLine("ai", p.text);
                }
            });
        }
        if (sc.turnComplete) {
            closeLogLines();
            // Let any buffered audio finish, then return to a listening state.
            if (!queued.length) { setStatus("listening", "Listening"); caption("Listening… ask about the feed"); }
        }
    }

    function readMessage(data) {
        if (typeof data === "string") {
            try { handleServerMessage(JSON.parse(data)); } catch (e) { }
        } else if (data instanceof Blob) {
            data.text().then(function (t) { try { handleServerMessage(JSON.parse(t)); } catch (e) { } });
        } else if (data instanceof ArrayBuffer) {
            try { handleServerMessage(JSON.parse(new TextDecoder().decode(data))); } catch (e) { }
        }
    }

    function fail(msg, detail) {
        if (detail !== undefined) console.error("[live] " + msg, detail);
        else console.error("[live] " + msg);
        caption("⚠ " + msg);
        setStatus("error", "Error");
        stopSession();
    }

    async function startSession() {
        var key = keyEl.value.trim();
        if (!key) { setSettings(true); caption("Add your Google AI Studio key first."); return; }
        var model = modelEl.value;
        if (!model) { setSettings(true); caption("Pick a live audio model first."); return; }

        setStatus("connecting", "Connecting");
        caption("Requesting microphone…");

        try {
            micStream = await navigator.mediaDevices.getUserMedia({
                audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true }
            });
        } catch (e) {
            return fail("Microphone permission denied.");
        }

        try {
            inCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: INPUT_RATE });
            outCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: OUTPUT_RATE });
            await outCtx.resume();
        } catch (e) {
            return fail("Audio isn't supported in this browser.");
        }

        caption("Connecting to Gemini…");
        ws = new WebSocket(WS_BASE + "?key=" + encodeURIComponent(key));
        ws.binaryType = "arraybuffer";

        ws.onopen = function () {
            var setupMsg = {
                setup: {
                    model: model.indexOf("models/") === 0 ? model : "models/" + model,
                    generationConfig: { responseModalities: ["AUDIO"] },
                    systemInstruction: { parts: [{ text: GROUND_PROMPT }] },
                    tools: [{ functionDeclarations: [SEARCH_DECL] }],
                    inputAudioTranscription: {},
                    outputAudioTranscription: {}
                }
            };
            console.log("[live] connected; sending setup for model:", setupMsg.setup.model);
            if (window.LIVE_DEBUG) console.debug("[live] >> setup", setupMsg);
            ws.send(JSON.stringify(setupMsg));
            startMic();
        };
        ws.onmessage = function (ev) { readMessage(ev.data); };
        ws.onerror = function (ev) {
            // The WebSocket error event is intentionally detail-free for security; the real cause
            // (unsupported model, bad key, quota, invalid argument) arrives in the close frame below,
            // so just log here and let onclose surface ev.reason.
            console.error("[live] WebSocket error event (the close code/reason next has the real cause):", ev);
        };
        ws.onclose = function (ev) {
            console.warn("[live] WebSocket closed — code:", ev.code, "reason:", ev.reason || "(none)", "wasClean:", ev.wasClean);
            if (!running) return;                              // we closed it ourselves via stopSession()
            if (ev.code === 1000) { stopSession(); return; }   // normal close — no error banner
            // 1007 invalid payload, 1008 policy/auth, 1011 server error — Gemini puts the
            // human-readable cause in ev.reason (e.g. model not found, API key invalid).
            var why = ev.reason ? ev.reason : ("connection closed unexpectedly (code " + ev.code + ")");
            fail("Gemini closed the session: " + why, { code: ev.code, reason: ev.reason });
        };

        running = true;
        micBtn.classList.add("is-live");
        micBtn.setAttribute("aria-label", "Stop");
    }

    function startMic() {
        srcNode = inCtx.createMediaStreamSource(micStream);
        // ScriptProcessor is deprecated but universally available and adequate for 16 kHz capture.
        procNode = inCtx.createScriptProcessor(2048, 1, 1);
        procNode.onaudioprocess = function (e) {
            if (!ws || ws.readyState !== WebSocket.OPEN) return;
            var input = e.inputBuffer.getChannelData(0); // Float32 @ 16 kHz
            var pcm = new Int16Array(input.length);
            for (var i = 0; i < input.length; i++) {
                var s = Math.max(-1, Math.min(1, input[i]));
                pcm[i] = s < 0 ? s * 0x8000 : s * 0x7fff;
            }
            // Live API: stream mic audio via realtimeInput.audio (a single Blob). The older
            // realtimeInput.mediaChunks form is deprecated and rejected with close code 1007 by
            // current models (e.g. Gemini 3.1 Flash Live).
            ws.send(JSON.stringify({
                realtimeInput: { audio: { mimeType: "audio/pcm;rate=" + INPUT_RATE, data: int16ToB64(pcm) } }
            }));
        };
        srcNode.connect(procNode);
        procNode.connect(inCtx.destination); // keep the node alive (it outputs silence)
    }

    function stopSession() {
        running = false;
        micBtn.classList.remove("is-live");
        micBtn.setAttribute("aria-label", "Start talking");
        caption("Tap the mic and ask about the feed");
        if (statusEl.getAttribute("data-state") !== "error") setStatus("idle", "Idle");

        stopPlayback();
        try { if (procNode) procNode.disconnect(); } catch (e) { }
        try { if (srcNode) srcNode.disconnect(); } catch (e) { }
        try { if (ws && ws.readyState <= 1) ws.close(); } catch (e) { }
        if (micStream) micStream.getTracks().forEach(function (t) { try { t.stop(); } catch (e) { } });
        try { if (inCtx) inCtx.close(); } catch (e) { }
        try { if (outCtx) outCtx.close(); } catch (e) { }
        ws = micStream = inCtx = outCtx = procNode = srcNode = null;
    }

    micBtn.addEventListener("click", function () {
        if (running) stopSession();
        else startSession();
    });
    window.addEventListener("beforeunload", function () { if (running) stopSession(); });
})();
