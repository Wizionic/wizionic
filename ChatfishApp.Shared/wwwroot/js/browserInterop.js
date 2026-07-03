window.chatfishBrowser = window.chatfishBrowser || {
    getContentBounds: function (selector) {
        const el = document.querySelector(selector);
        if (!el) return null;
        const r = el.getBoundingClientRect();
        if (r.width <= 0 || r.height <= 0) return null;
        return { x: r.left, y: r.top, width: r.width, height: r.height };
    },

    _observer: null,
    _observedEl: null,
    _resizeHandler: null,
    _debounceTimer: null,
    _dotNetRef: null,
    _lastBoundsKey: null,
    _activeSelector: null,

    startBoundsObserver: function (selector, dotNetRef) {
        if (this._activeSelector === selector && this._dotNetRef === dotNetRef && this._observer)
            return;

        this.stopBoundsObserver();
        const el = document.querySelector(selector);
        if (!el || !dotNetRef) {
            console.warn('[Browser] startBoundsObserver: element or dotNetRef missing for', selector);
            return;
        }

        this._dotNetRef = dotNetRef;
        this._activeSelector = selector;

        const sendBounds = () => {
            const bounds = this.getContentBounds(selector);
            if (!bounds || !this._dotNetRef)
                return false;

            const key = [bounds.x, bounds.y, bounds.width, bounds.height]
                .map(v => Math.round(v))
                .join(',');
            if (key === this._lastBoundsKey)
                return true;

            this._lastBoundsKey = key;
            this._dotNetRef.invokeMethodAsync(
                'OnBrowserOverlayBounds',
                bounds.x, bounds.y, bounds.width, bounds.height);
            return true;
        };

        const report = () => {
            if (this._debounceTimer)
                clearTimeout(this._debounceTimer);

            this._debounceTimer = setTimeout(() => sendBounds(), 50);
        };

        sendBounds();
        this._observedEl = el;
        this._observer = new ResizeObserver(() => report());
        this._observer.observe(el);
        this._resizeHandler = report;
        window.addEventListener('resize', report);
    },

    reportBoundsNow: function (selector) {
        if (!this._dotNetRef || !selector)
            return;
        const bounds = this.getContentBounds(selector);
        if (!bounds)
            return;
        this._lastBoundsKey = null;
        this._dotNetRef.invokeMethodAsync(
            'OnBrowserOverlayBounds',
            bounds.x, bounds.y, bounds.width, bounds.height);
    },

    setResizeCursor: function (active) {
        document.body.style.cursor = active ? 'ew-resize' : '';
    },

    getWrapperWidth: function (selector) {
        const el = document.querySelector(selector);
        if (!el) return 0;
        return el.getBoundingClientRect().width;
    },

    startSplitterDrag: function (dotNetRef, wrapperSelector) {
        const wrapper = document.querySelector(wrapperSelector);
        if (!wrapper || !dotNetRef) return;

        const onMove = (e) => {
            const rect = wrapper.getBoundingClientRect();
            const x = e.clientX - rect.left;
            dotNetRef.invokeMethodAsync('OnSplitterDrag', x);
        };

        const onUp = () => {
            document.removeEventListener('mousemove', onMove);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        };

        document.body.style.cursor = 'ew-resize';
        document.body.style.userSelect = 'none';
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp, { once: true });
    },

    stopBoundsObserver: function () {
        if (this._debounceTimer) {
            clearTimeout(this._debounceTimer);
            this._debounceTimer = null;
        }
        if (this._observer) {
            this._observer.disconnect();
            this._observer = null;
        }
        if (this._resizeHandler) {
            window.removeEventListener('resize', this._resizeHandler);
            this._resizeHandler = null;
        }
        this._observedEl = null;
        this._dotNetRef = null;
        this._lastBoundsKey = null;
        this._activeSelector = null;
    }
};