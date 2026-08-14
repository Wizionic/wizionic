// Positions the MAUI native URL-embed WebView over a Blazor host element.
// Exposed as appUrlEmbed (matches appTheme / appBrowser). Alias kept for older callers.
window.appUrlEmbed = window.wizionicUrlEmbed = (function () {
  const observers = new WeakMap();

  function measureEl(el) {
    if (!el || !el.getBoundingClientRect) return null;
    const r = el.getBoundingClientRect();
    // Device-independent pixels relative to the visual viewport (matches browser overlays).
    const dpr = window.devicePixelRatio || 1;
    // MAUI AbsoluteLayout uses DIP; getBoundingClientRect is CSS px (already DIP on most hosts).
    return {
      x: r.left,
      y: r.top,
      width: r.width,
      height: r.height,
      dpr
    };
  }

  function report(el, dotNet) {
    if (!el || !dotNet) return;
    const m = measureEl(el);
    if (!m || m.width < 2 || m.height < 2) {
      try { dotNet.invokeMethodAsync("OnBounds", 0, 0, 0, 0); } catch (_) {}
      return;
    }
    try {
      dotNet.invokeMethodAsync("OnBounds", m.x, m.y, m.width, m.height);
    } catch (_) {}
  }

  return {
    attach: function (el, dotNet) {
      if (!el || !dotNet) return;
      const run = () => report(el, dotNet);
      run();

      const ro = typeof ResizeObserver !== "undefined"
        ? new ResizeObserver(() => run())
        : null;
      if (ro) ro.observe(el);

      const onWin = () => run();
      window.addEventListener("resize", onWin);
      window.addEventListener("scroll", onWin, true);

      observers.set(el, { ro, onWin, dotNet });
      // Second pass after layout settles.
      requestAnimationFrame(() => requestAnimationFrame(run));
    },

    measure: function (el) {
      const st = observers.get(el);
      if (st) report(el, st.dotNet);
    },

    detach: function (el) {
      const st = observers.get(el);
      if (!st) return;
      try {
        if (st.ro) st.ro.disconnect();
        window.removeEventListener("resize", st.onWin);
        window.removeEventListener("scroll", st.onWin, true);
      } catch (_) {}
      observers.delete(el);
    }
  };
})();
