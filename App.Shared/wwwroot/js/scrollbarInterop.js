// Scrollbar reveal helper: marks actively scrolling elements with [data-scrolling]
// so the thin thumb is visible during touch/mouse scrolls, then fades after idle.
// Hover reveal is pure CSS (see app.css SCROLLBARS section).
(function () {
    var timers = new WeakMap();

    function mark(el) {
        if (!el || el.nodeType !== 1) return;
        el.setAttribute('data-scrolling', '1');
        var existing = timers.get(el);
        if (existing) clearTimeout(existing);
        timers.set(el, setTimeout(function () {
            el.removeAttribute('data-scrolling');
            timers.delete(el);
        }, 900));
    }

    // Scroll events don't bubble, but they do capture — one listener covers every scroller.
    document.addEventListener('scroll', function (e) {
        var t = e.target;
        mark(t === document || t === window ? document.documentElement : t);
    }, { capture: true, passive: true });
})();