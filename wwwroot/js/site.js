(function () {
    "use strict";

    // ---------- Theme toggle: cycles system → light → dark → system ----------
    // "system" leaves data-theme unset so the CSS follows prefers-color-scheme; light/dark pin it.
    // The stored mode drives both the colors and which icon (monitor/sun/moon) the button shows.
    var root = document.documentElement;
    var toggle = document.getElementById("theme-toggle");
    var MODES = ["system", "light", "dark"];

    function currentMode() {
        var m = root.getAttribute("data-theme-mode");
        return MODES.indexOf(m) >= 0 ? m : "system";
    }

    function applyMode(mode) {
        root.setAttribute("data-theme-mode", mode);
        if (mode === "light" || mode === "dark") root.setAttribute("data-theme", mode);
        else root.removeAttribute("data-theme");           // system → follow the OS
        try { localStorage.setItem("theme", mode); } catch (e) { }
        if (toggle) {
            var label = "Theme: " + mode + " (click to change)";
            toggle.setAttribute("aria-label", label);
            toggle.setAttribute("title", label);
        }
    }

    if (toggle) {
        applyMode(currentMode());                          // sync label to the pre-paint state
        toggle.addEventListener("click", function () {
            var next = MODES[(MODES.indexOf(currentMode()) + 1) % MODES.length];
            applyMode(next);
        });
    }

    // ---------- Relative time ----------
    function relative(date) {
        var diff = (Date.now() - date.getTime()) / 1000; // seconds
        if (diff < 60) return "just now";
        var mins = Math.floor(diff / 60);
        if (mins < 60) return mins + "m ago";
        var hrs = Math.floor(mins / 60);
        if (hrs < 24) return hrs + "h ago";
        var days = Math.floor(hrs / 24);
        if (days < 30) return days + "d ago";
        return date.toLocaleDateString();
    }

    document.querySelectorAll("time[datetime]").forEach(function (el) {
        var d = new Date(el.getAttribute("datetime"));
        if (isNaN(d.getTime())) return;
        el.textContent = relative(d);
        el.title = d.toLocaleString();
    });
})();
