(function () {
    "use strict";

    // ---------- Theme toggle (default: follow system) ----------
    var root = document.documentElement;
    var toggle = document.getElementById("theme-toggle");

    function effectiveTheme() {
        var explicit = root.getAttribute("data-theme");
        if (explicit) return explicit;
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    }

    if (toggle) {
        toggle.addEventListener("click", function () {
            var next = effectiveTheme() === "dark" ? "light" : "dark";
            root.setAttribute("data-theme", next);
            try { localStorage.setItem("theme", next); } catch (e) { }
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
