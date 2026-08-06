// Cross-component sidebar state for Blazor WebAssembly.
// AppTopBar is statically rendered (no @rendermode) so Blazor @onclick never fires there.
// This module owns click handling via document delegation, DOM classes, and one .NET listener.
// Storage key is namespaced per user: {prefix}app-sidebar-collapsed (see setStoragePrefix).
window.appSidebar = (function () {
    const BASE_STORAGE_KEY = 'app-sidebar-collapsed';
    const LOG_PREFIX = '[appSidebar]';
    const DEBUG = false;

    let storagePrefix = '';
    let activeListener = null;
    let collapsedCache = null;
    let clickDelegationBound = false;

    function storageKey() {
        return (storagePrefix || '') + BASE_STORAGE_KEY;
    }

    function setStoragePrefix(prefix) {
        const next = prefix || '';
        if (storagePrefix === next)
            return;
        storagePrefix = next;
        // Force re-read from the new namespace on next getCollapsed.
        collapsedCache = null;
    }

    function log(step, detail) {
        if (!DEBUG) return;
        const ts = new Date().toISOString().split('T')[1];
        if (detail === undefined) {
            console.log(LOG_PREFIX, ts, step);
        } else {
            console.log(LOG_PREFIX, ts, step, detail);
        }
    }

    function readStorage() {
        try {
            const key = storageKey();
            let value = localStorage.getItem(key);
            if (value === null && key !== BASE_STORAGE_KEY) {
                // Seed from legacy unprefixed key once.
                const legacy = localStorage.getItem(BASE_STORAGE_KEY);
                if (legacy === '1' || legacy === '0') {
                    localStorage.setItem(key, legacy);
                    value = legacy;
                }
            }
            if (value === '1') return true;
            if (value === '0') return false;
        } catch (_) { /* private browsing */ }
        return null;
    }

    function writeStorage(collapsed) {
        try {
            localStorage.setItem(storageKey(), collapsed ? '1' : '0');
        } catch (_) { /* private browsing */ }
    }

    function findPageEl() {
        return document.querySelector('.app-body .page') || document.querySelector('.page');
    }

    function findToggleButton() {
        return document.querySelector('[data-wasm-sidebar-toggle]')
            || document.querySelector('.topbar-sidebar-toggle');
    }

    function applyToggleButtonUi(collapsed) {
        const btn = findToggleButton();
        if (!btn) return;

        const expandedIcon = btn.querySelector('.sidebar-toggle-icon--expanded');
        const collapsedIcon = btn.querySelector('.sidebar-toggle-icon--collapsed');

        if (collapsed) {
            btn.classList.remove('active');
            if (expandedIcon) expandedIcon.style.display = 'none';
            if (collapsedIcon) collapsedIcon.style.display = '';
        } else {
            btn.classList.add('active');
            if (expandedIcon) expandedIcon.style.display = '';
            if (collapsedIcon) collapsedIcon.style.display = 'none';
        }
    }

    function applyDom(collapsed) {
        const pageEl = findPageEl();
        const sidebar = pageEl ? pageEl.querySelector('.sidebar') : document.querySelector('.sidebar');
        const mainEl = pageEl ? pageEl.querySelector('main') : document.querySelector('main');

        if (pageEl) {
            if (collapsed) {
                pageEl.classList.add('sidebar-collapsed');
            } else {
                pageEl.classList.remove('sidebar-collapsed');
            }
        }

        if (sidebar) sidebar.style.display = '';
        if (mainEl) mainEl.style.marginLeft = '';

        applyToggleButtonUi(collapsed);
    }

    function clearActiveListener(reason) {
        if (activeListener) {
            log('clearActiveListener', { reason: reason });
            activeListener = null;
        }
    }

    function notifyListener(collapsed, source) {
        const ref = activeListener;
        if (!ref) return;

        log('notifyListener', { collapsed: collapsed, source: source });

        ref.invokeMethodAsync('OnSidebarCollapsedChanged', collapsed)
            .catch(function (err) {
                log('notifyListener:stale', {
                    source: source,
                    error: err && err.message ? err.message : err
                });
                clearActiveListener('invoke failed');
            });
    }

    function getCollapsed() {
        if (collapsedCache !== null) return collapsedCache;

        const stored = readStorage();
        if (stored !== null) {
            collapsedCache = stored;
            return stored;
        }

        const pageEl = findPageEl();
        if (pageEl) {
            collapsedCache = pageEl.classList.contains('sidebar-collapsed');
            return collapsedCache;
        }

        return false;
    }

    function setCollapsed(collapsed, options) {
        const next = !!collapsed;
        const source = (options && options.source) || 'setCollapsed';
        const skipNotify = options && options.skipNotify;

        if (collapsedCache === next && skipNotify) {
            applyDom(next);
            return;
        }

        collapsedCache = next;
        writeStorage(next);
        applyDom(next);

        if (!skipNotify) {
            notifyListener(next, source);
        }
    }

    function registerListener(dotNetRef, tag) {
        if (!dotNetRef) return;

        if (activeListener && activeListener !== dotNetRef) {
            log('registerListener:replace', { tag: tag || 'unknown' });
        }

        activeListener = dotNetRef;
        log('registerListener', { tag: tag || 'unknown' });
    }

    function unregisterListener(dotNetRef, tag) {
        if (!dotNetRef) return;

        if (activeListener === dotNetRef) {
            clearActiveListener('unregister ' + (tag || 'unknown'));
        }
    }

    function onToggleClick(event) {
        const btn = event.target && event.target.closest
            ? event.target.closest('[data-wasm-sidebar-toggle], .topbar-sidebar-toggle')
            : null;

        if (!btn) return;

        // Only handle WASM buttons (data attribute marks intent). MAUI uses Blazor @onclick.
        if (!btn.hasAttribute('data-wasm-sidebar-toggle')) return;

        event.preventDefault();
        event.stopPropagation();

        setCollapsed(!getCollapsed(), { source: 'delegatedClick' });
    }

    function ensureClickDelegation() {
        if (clickDelegationBound) return;

        document.addEventListener('click', onToggleClick, true);
        clickDelegationBound = true;
        log('ensureClickDelegation:bound');
    }

    function initForViewport() {
        if (typeof window.isMobileViewport === 'function' && window.isMobileViewport()) {
            setCollapsed(true, { source: 'initForViewport' });
            return true;
        }
        return false;
    }

    return {
        setStoragePrefix: setStoragePrefix,
        getCollapsed: getCollapsed,
        setCollapsed: setCollapsed,
        registerListener: registerListener,
        unregisterListener: unregisterListener,
        initForViewport: initForViewport,
        ensureClickDelegation: ensureClickDelegation,
        applyToggleButtonUi: applyToggleButtonUi
    };
})();

window.toggleWasmSidebar = function (collapsed) {
    window.appSidebar.setCollapsed(collapsed, { source: 'toggleWasmSidebar' });
};

window.initWasmSidebarForViewport = function () {
    return window.appSidebar.initForViewport();
};

window.appSidebar.ensureClickDelegation();