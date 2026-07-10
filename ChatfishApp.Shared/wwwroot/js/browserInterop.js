window.chatfishBrowser = window.chatfishBrowser || {
    getContentBounds: function (selector) {
        const el = document.querySelector(selector);
        if (!el) return null;
        const r = el.getBoundingClientRect();
        if (r.width <= 0 || r.height <= 0) return null;
        return { x: r.left, y: r.top, width: r.width, height: r.height };
    },

    _observer: null,
    _observedEls: null,
    _resizeHandler: null,
    _debounceTimer: null,
    _dotNetRef: null,
    _lastMainBoundsKey: null,
    _lastSideBoundsKey: null,
    _mainSelector: null,
    _sideSelector: null,

    startBoundsObserver: function (mainSelector, sideSelector, dotNetRef) {
        if (this._mainSelector === mainSelector
            && this._sideSelector === sideSelector
            && this._dotNetRef === dotNetRef
            && this._observer)
            return;

        this.stopBoundsObserver();
        const mainEl = document.querySelector(mainSelector);
        if (!mainEl || !dotNetRef) {
            console.warn('[Browser] startBoundsObserver: main element or dotNetRef missing for', mainSelector);
            return;
        }

        this._dotNetRef = dotNetRef;
        this._mainSelector = mainSelector;
        this._sideSelector = sideSelector;

        const sendBounds = () => {
            if (!this._dotNetRef)
                return;

            const main = this.getContentBounds(mainSelector);
            if (main) {
                const mainKey = [main.x, main.y, main.width, main.height]
                    .map(v => Math.round(v))
                    .join(',');
                if (mainKey !== this._lastMainBoundsKey) {
                    this._lastMainBoundsKey = mainKey;
                    this._dotNetRef.invokeMethodAsync(
                        'OnBrowserMainOverlayBounds',
                        main.x, main.y, main.width, main.height);
                }
            }

            const side = sideSelector ? this.getContentBounds(sideSelector) : null;
            const sideKey = side
                ? [side.x, side.y, side.width, side.height].map(v => Math.round(v)).join(',')
                : '0,0,0,0';
            if (sideKey !== this._lastSideBoundsKey) {
                this._lastSideBoundsKey = sideKey;
                if (side) {
                    this._dotNetRef.invokeMethodAsync(
                        'OnBrowserSideOverlayBounds',
                        side.x, side.y, side.width, side.height);
                } else {
                    this._dotNetRef.invokeMethodAsync('OnBrowserSideOverlayBounds', 0, 0, 0, 0);
                }
            }
        };

        const report = () => {
            if (this._debounceTimer)
                clearTimeout(this._debounceTimer);
            this._debounceTimer = setTimeout(() => sendBounds(), 50);
        };

        sendBounds();
        this._observedEls = [mainEl];
        const sideEl = sideSelector ? document.querySelector(sideSelector) : null;
        if (sideEl)
            this._observedEls.push(sideEl);

        // Also watch the browser body / side column so opening the settings/bookmarks
        // panel (which shrinks the main host via flex) always re-reports main bounds.
        const bodyEl = document.getElementById('browser-body');
        if (bodyEl)
            this._observedEls.push(bodyEl);
        const sideCol = document.querySelector('.browser-side-column');
        if (sideCol)
            this._observedEls.push(sideCol);

        this._observer = new ResizeObserver(() => report());
        this._observedEls.forEach(el => this._observer.observe(el));
        this._resizeHandler = report;
        window.addEventListener('resize', report);

        // Second tick after layout settles (side panel open/close).
        setTimeout(() => sendBounds(), 0);
        setTimeout(() => sendBounds(), 100);
    },

    reportBoundsNow: function (mainSelector, sideSelector) {
        if (!this._dotNetRef)
            return;

        this._lastMainBoundsKey = null;
        this._lastSideBoundsKey = null;

        const main = this.getContentBounds(mainSelector);
        if (main) {
            this._dotNetRef.invokeMethodAsync(
                'OnBrowserMainOverlayBounds',
                main.x, main.y, main.width, main.height);
        }

        const side = sideSelector ? this.getContentBounds(sideSelector) : null;
        if (side) {
            this._dotNetRef.invokeMethodAsync(
                'OnBrowserSideOverlayBounds',
                side.x, side.y, side.width, side.height);
        } else {
            this._dotNetRef.invokeMethodAsync('OnBrowserSideOverlayBounds', 0, 0, 0, 0);
        }
    },

    setResizeCursor: function (active) {
        document.body.style.cursor = active ? 'ew-resize' : '';
    },

    getPanelAnchor: function (panelSelector, clientX, clientY) {
        const panel = document.querySelector(panelSelector);
        if (!panel)
            return [clientX, clientY];
        const rect = panel.getBoundingClientRect();
        return [clientX - rect.left, clientY - rect.top];
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

    startSidePanelSplitterDrag: function (dotNetRef, bodySelector) {
        const body = document.querySelector(bodySelector);
        if (!body || !dotNetRef) return;

        const onMove = (e) => {
            const rect = body.getBoundingClientRect();
            const toolbar = body.querySelector('.browser-vtoolbar');
            const toolbarWidth = toolbar ? toolbar.getBoundingClientRect().width : 48;
            const sideWidth = rect.right - e.clientX - toolbarWidth;
            dotNetRef.invokeMethodAsync('OnSidePanelDrag', sideWidth);
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

    startBookmarkBarDrag: function (dotNetRef, barSelector, bookmarkId, startX, startY) {
        const bar = document.querySelector(barSelector);
        if (!bar || !dotNetRef || !bookmarkId)
            return;

        const threshold = 5;
        let dragStarted = false;

        const getItemAt = (x, y) => {
            const el = document.elementFromPoint(x, y);
            return el?.closest('[data-bookmark-id]');
        };

        const onMove = (e) => {
            if (!dragStarted) {
                const dx = Math.abs(e.clientX - startX);
                const dy = Math.abs(e.clientY - startY);
                if (dx < threshold && dy < threshold)
                    return;
                dragStarted = true;
                document.body.style.userSelect = 'none';
            }

            const item = getItemAt(e.clientX, e.clientY);
            const overId = item?.getAttribute('data-bookmark-id') || null;
            dotNetRef.invokeMethodAsync('OnBookmarkBarDragOver', overId);
        };

        const onUp = (e) => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.userSelect = '';

            if (!dragStarted) {
                dotNetRef.invokeMethodAsync('OnBookmarkBarClick', bookmarkId);
                return;
            }

            const item = getItemAt(e.clientX, e.clientY);
            const targetId = item?.getAttribute('data-bookmark-id') || '';
            dotNetRef.invokeMethodAsync('OnBookmarkBarDrop', bookmarkId, targetId);
        };

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    },

    startVtoolbarDrag: function (dotNetRef, toolbarSelector, appId, startX, startY) {
        const toolbar = document.querySelector(toolbarSelector);
        if (!toolbar || !dotNetRef || !appId)
            return;

        const threshold = 5;
        let dragStarted = false;

        const getAppAt = (x, y) => {
            const el = document.elementFromPoint(x, y);
            return el?.closest('[data-vtoolbar-app-id]');
        };

        const onMove = (e) => {
            if (!dragStarted) {
                const dx = Math.abs(e.clientX - startX);
                const dy = Math.abs(e.clientY - startY);
                if (dx < threshold && dy < threshold)
                    return;
                dragStarted = true;
                document.body.style.userSelect = 'none';
            }

            const item = getAppAt(e.clientX, e.clientY);
            const overId = item?.getAttribute('data-vtoolbar-app-id') || null;
            dotNetRef.invokeMethodAsync('OnVtoolbarDragOver', overId);
        };

        const onUp = (e) => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.userSelect = '';

            if (!dragStarted) {
                dotNetRef.invokeMethodAsync('OnVtoolbarAppClick', appId);
                return;
            }

            const item = getAppAt(e.clientX, e.clientY);
            const targetId = item?.getAttribute('data-vtoolbar-app-id') || '';
            dotNetRef.invokeMethodAsync('OnVtoolbarAppDrop', appId, targetId);
        };

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    },

    startSidebarDrag: function (dotNetRef, listSelector, kind, itemId, folderId, startX, startY) {
        const list = document.querySelector(listSelector);
        if (!list || !dotNetRef || !itemId || !kind)
            return;

        const threshold = 5;
        let dragStarted = false;

        const getDropTarget = (x, y) => {
            const el = document.elementFromPoint(x, y);
            const bookmarkRow = el?.closest('[data-sidebar-bookmark-id]');
            if (bookmarkRow) {
                return {
                    type: 'bookmark',
                    id: bookmarkRow.getAttribute('data-sidebar-bookmark-id'),
                    folderId: bookmarkRow.getAttribute('data-sidebar-folder-id')
                };
            }

            const folderHeader = el?.closest('[data-sidebar-folder-drop]');
            if (folderHeader) {
                const folder = folderHeader.closest('[data-sidebar-folder-id]');
                const folderId = folder?.getAttribute('data-sidebar-folder-id');
                if (folderId)
                    return { type: 'folder', id: folderId, folderId };
            }

            const folderBlock = el?.closest('[data-sidebar-folder-id]');
            if (folderBlock && kind === 'folder') {
                const folderId = folderBlock.getAttribute('data-sidebar-folder-id');
                if (folderId)
                    return { type: 'folder', id: folderId, folderId };
            }

            return null;
        };

        const onMove = (e) => {
            if (!dragStarted) {
                const dx = Math.abs(e.clientX - startX);
                const dy = Math.abs(e.clientY - startY);
                if (dx < threshold && dy < threshold)
                    return;
                dragStarted = true;
                document.body.style.userSelect = 'none';
            }

            const target = getDropTarget(e.clientX, e.clientY);
            dotNetRef.invokeMethodAsync(
                'OnSidebarDragOver',
                target?.type || null,
                target?.id || null,
                target?.folderId || null);
        };

        const onUp = (e) => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.userSelect = '';

            if (!dragStarted) {
                if (kind === 'bookmark')
                    dotNetRef.invokeMethodAsync('OnSidebarBookmarkClick', itemId);
                return;
            }

            const target = getDropTarget(e.clientX, e.clientY);
            dotNetRef.invokeMethodAsync(
                'OnSidebarDrop',
                kind,
                itemId,
                folderId,
                target?.type || null,
                target?.id || null,
                target?.folderId || null);
        };

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
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
        this._observedEls = null;
        this._dotNetRef = null;
        this._lastMainBoundsKey = null;
        this._lastSideBoundsKey = null;
        this._mainSelector = null;
        this._sideSelector = null;
    }
};