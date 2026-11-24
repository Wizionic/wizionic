// Zoom level indicator: transient badge on Ctrl+/-/0, Ctrl+wheel, or pinch zoom.
// Percent is relative to a stored baseline devicePixelRatio (first run = 100%).
// Ctrl+0 resets browser zoom to 100% and re-anchors the baseline, so the badge
// stays accurate even if the app was first loaded at a non-100% zoom.
(function () {
    var BASE_KEY = 'app-zoom-baseline-dpr';
    var badge = null, hideTimer = null, lastShown = null, base = null;

    function readBaseline() {
        var dpr = window.devicePixelRatio || 1;
        try {
            var stored = parseFloat(localStorage.getItem(BASE_KEY));
            if (stored > 0) return stored;
            localStorage.setItem(BASE_KEY, String(dpr));
        } catch (e) { /* private browsing */ }
        return dpr;
    }

    function saveBaseline(dpr) {
        base = dpr;
        try { localStorage.setItem(BASE_KEY, String(dpr)); } catch (e) { }
    }

    function percent() {
        if (base == null) base = readBaseline();
        var dpr = window.devicePixelRatio || 1;
        var vv = window.visualViewport ? (window.visualViewport.scale || 1) : 1;
        var raw = (dpr / base) * vv * 100;
        // Snap to common browser zoom steps to avoid 99%/101% noise.
        var steps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];
        for (var i = 0; i < steps.length; i++) {
            if (Math.abs(raw - steps[i]) <= 2) return steps[i];
        }
        return Math.round(raw);
    }

    function ensureBadge() {
        if (badge && document.body && document.body.contains(badge)) return;
        badge = document.createElement('div');
        badge.className = 'app-zoom-badge';
        badge.setAttribute('role', 'status');
        document.body.appendChild(badge);
    }

    function show(force) {
        var p = percent();
        if (!force && p === lastShown) return;
        lastShown = p;
        ensureBadge();
        badge.textContent = 'Zoom ' + p + '%';
        badge.classList.add('is-visible');
        if (hideTimer) clearTimeout(hideTimer);
        hideTimer = setTimeout(function () { badge.classList.remove('is-visible'); }, 1600);
    }

    window.addEventListener('resize', function () { show(false); });
    if (window.visualViewport) {
        window.visualViewport.addEventListener('resize', function () { show(false); });
    }

    document.addEventListener('keydown', function (e) {
        if (!(e.ctrlKey || e.metaKey)) return;
        if (e.key === '+' || e.key === '-' || e.key === '=') {
            setTimeout(function () { show(false); }, 60);
        } else if (e.key === '0') {
            // Browser reset to 100% — re-anchor the baseline once zoom applies.
            setTimeout(function () {
                saveBaseline(window.devicePixelRatio || 1);
                lastShown = null;
                show(true);
            }, 120);
        }
    });

    // Suppress a badge flash on the first ordinary window resize after load.
    lastShown = percent();

    window.appZoom = {
        show: function () { show(true); },
        percent: percent
    };
})();
