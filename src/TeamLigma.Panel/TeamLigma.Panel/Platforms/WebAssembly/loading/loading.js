// Team Ligma Development Panel - loading screen controller.
// Mirrors the Uno bootstrapper's download progress into the themed bar and fades the
// overlay out once the Uno app has rendered.
(function () {
    "use strict";

    const TIPS = [
        "A full clean build is Clean, then Prepare (CMake), then Start Build. The install runs on its own when the compile succeeds.",
        "The Authserver console listens for one thing only: Ctrl + C.",
        "Leave CPU threads on Auto to keep one core free for your realm while it builds.",
        "Builds started from a shell are spotted too. They appear in History, marked external.",
        "Turn on the authenticator app under My Account, and keep your recovery codes somewhere safe.",
        "Upload a .sql script to the MySQL console with Run .sql script.",
        "Shut the Worldserver down gracefully with: server shutdown 0h0m10s",
        "Several builds can run at once, as long as each has its own build directory.",
        "Every build keeps its full log. Open it from History with View Log.",
        "A Viewer can watch every build and console, but cannot touch a single lever.",
    ];

    const DOWNLOAD_SHARE = 0.85;   // downloads fill the bar to 85%
    const STARTUP_CEILING = 0.97;  // runtime start-up creeps towards this until the app appears

    let root, fill, pct, phase, tipBox, tipText;
    let shown = 0, target = 0, finished = false, startupSince = 0, tipIndex = 0;

    function ready(fn) {
        if (document.readyState !== "loading") fn();
        else document.addEventListener("DOMContentLoaded", fn);
    }

    ready(function () {
        root = document.getElementById("tl-loading");
        if (!root) return;
        fill = document.getElementById("tl-fill");
        pct = document.getElementById("tl-pct");
        phase = document.getElementById("tl-phase");
        tipBox = root.querySelector(".tl-tip");
        tipText = document.getElementById("tl-tip-text");

        spawnEmbers();
        tipIndex = Math.floor(Math.random() * TIPS.length);
        tipText.textContent = TIPS[tipIndex];
        setInterval(nextTip, 6500);
        requestAnimationFrame(tick);
    });

    function spawnEmbers() {
        if (window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
        const box = root.querySelector(".tl-embers");
        const count = Math.round(Math.min(48, Math.max(18, window.innerWidth / 32)));
        for (let i = 0; i < count; i++) {
            const e = document.createElement("span");
            e.className = "tl-ember";
            const size = 2 + Math.random() * 4;
            e.style.setProperty("--x", (Math.random() * 100).toFixed(2) + "%");
            e.style.setProperty("--s", size.toFixed(1) + "px");
            e.style.setProperty("--d", (7 + Math.random() * 9).toFixed(2) + "s");
            e.style.setProperty("--delay", (-Math.random() * 14).toFixed(2) + "s");
            e.style.setProperty("--drift", ((Math.random() - 0.5) * 220).toFixed(0) + "px");
            e.style.setProperty("--o", (0.35 + Math.random() * 0.6).toFixed(2));
            box.appendChild(e);
        }
    }

    function nextTip() {
        if (finished) return;
        tipBox.classList.add("tl-fade");
        setTimeout(function () {
            tipIndex = (tipIndex + 1) % TIPS.length;
            tipText.textContent = TIPS[tipIndex];
            tipBox.classList.remove("tl-fade");
        }, 600);
    }

    // The Uno app has started rendering once the bootstrapper's loader is gone,
    // or something other than the loader has been added to #uno-body.
    function appHasRendered() {
        const loader = document.querySelector(".uno-loader");
        if (!loader) return true;
        const body = document.getElementById("uno-body");
        if (body) {
            for (const child of body.children) {
                if (!child.classList.contains("uno-loader")) return true;
            }
        }
        return false;
    }

    function tick(now) {
        if (finished) return;

        const progress = document.querySelector(".uno-loader progress");
        const alert = document.querySelector(".uno-loader .alert");

        if (alert && alert.textContent.trim()) {
            root.classList.add("tl-error");
            phase.textContent = alert.textContent.trim();
        } else if (appHasRendered()) {
            complete();
            return;
        } else if (progress && progress.max > 1 && progress.value > 0) {
            const loaded = Number(progress.value), total = Number(progress.max);
            if (loaded < total) {
                target = Math.max(target, DOWNLOAD_SHARE * (loaded / total));
                phase.textContent = "Gathering resources · " + loaded + " / " + total;
                startupSince = 0;
            } else {
                // downloads done: the .NET runtime and the app are starting
                if (!startupSince) startupSince = now;
                const t = (now - startupSince) / 1000;
                target = DOWNLOAD_SHARE + (STARTUP_CEILING - DOWNLOAD_SHARE) * (1 - Math.exp(-t / 3));
                phase.textContent = "Waking the runtime";
            }
        } else {
            // before the bootstrapper reports anything: a gentle creep so the bar is alive
            target = Math.max(target, Math.min(0.06, target + 0.0004));
        }

        render();
        requestAnimationFrame(tick);
    }

    function render() {
        shown += (target - shown) * 0.12;
        if (Math.abs(target - shown) < 0.0005) shown = target;
        const p = Math.max(0, Math.min(1, shown));
        fill.style.width = (p * 100).toFixed(2) + "%";
        pct.textContent = Math.floor(p * 100) + "%";
        root.setAttribute("aria-valuenow", String(Math.floor(p * 100)));
    }

    function complete() {
        finished = true;
        target = 1;
        phase.textContent = "Entering the realm";
        // finish the bar, give the app a moment to paint its first frame, then fade
        const start = performance.now();
        (function finish(now) {
            shown += (1 - shown) * 0.2;
            if (now - start > 450) shown = 1;
            const p = Math.min(1, shown);
            fill.style.width = (p * 100).toFixed(2) + "%";
            pct.textContent = Math.floor(p * 100) + "%";
            root.setAttribute("aria-valuenow", String(Math.floor(p * 100)));
            if (p < 1) requestAnimationFrame(finish);
            else setTimeout(fadeOut, 350);
        })(start);
    }

    function fadeOut() {
        root.classList.add("tl-done");
        setTimeout(function () { if (root && root.parentNode) root.parentNode.removeChild(root); }, 900);
    }
})();
