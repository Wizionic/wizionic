// Navigation bar layout + icon visibility prefs (WASM, MAUI WebView, host shell).
// Storage key is namespaced per user: {prefix}app-nav-layout (see setStoragePrefix).
// Value is either legacy "top"/"left" or JSON prefs object.
window.appNavLayout = window.appNavLayout || {

    baseStorageKey: 'app-nav-layout',
    storagePrefix: '',
    // Host may set before applyEarly (MAUI sets 'left').
    defaultMode: 'top',

    get storageKey() {
        return (this.storagePrefix || '') + this.baseStorageKey;
    },

    setStoragePrefix: function (prefix) {
        this.storagePrefix = prefix || '';
    },

    defaultPrefs: function () {
        return {
            mode: this.defaultMode === 'left' ? 'left' : 'top',
            showBrowser: true,
            showNotes: true,
            showGallery: true,
            showCalendar: true,
            // Settings cluster collapsed until the user expands it.
            secondaryExpanded: false
        };
    },

    normalizeMode: function (mode) {
        return mode === 'left' ? 'left' : 'top';
    },

    normalizePrefs: function (raw) {
        const d = this.defaultPrefs();
        if (raw == null || raw === '')
            return d;

        // Legacy plain string
        if (typeof raw === 'string') {
            const t = raw.trim();
            if (t === 'top' || t === 'left') {
                d.mode = t;
                return d;
            }
            try {
                raw = JSON.parse(t);
            } catch (e) {
                return d;
            }
        }

        if (typeof raw !== 'object')
            return d;

        return {
            mode: this.normalizeMode(raw.mode),
            showBrowser: raw.showBrowser !== false,
            showNotes: raw.showNotes !== false,
            showGallery: raw.showGallery !== false,
            showCalendar: raw.showCalendar !== false,
            // Default closed: only true when explicitly saved as true.
            secondaryExpanded: raw.secondaryExpanded === true
        };
    },

    readRaw: function () {
        const key = this.storageKey;
        try {
            let value = localStorage.getItem(key);
            if (value !== null)
                return value;

            if (key !== this.baseStorageKey) {
                const legacy = localStorage.getItem(this.baseStorageKey);
                if (legacy !== null) {
                    localStorage.setItem(key, legacy);
                    return legacy;
                }
            }
        } catch (e) { /* private browsing */ }
        return null;
    },

    getPrefs: function () {
        return this.normalizePrefs(this.readRaw());
    },

    getMode: function () {
        return this.getPrefs().mode;
    },

    savePrefs: function (prefs) {
        const normalized = this.normalizePrefs(prefs);
        try {
            localStorage.setItem(this.storageKey, JSON.stringify(normalized));
        } catch (e) { /* private browsing */ }
        return normalized;
    },

    saveMode: function (mode) {
        const prefs = this.getPrefs();
        prefs.mode = this.normalizeMode(mode);
        this.savePrefs(prefs);
    },

    apply: function (mode) {
        const root = document.documentElement;
        const normalized = this.normalizeMode(mode != null ? mode : this.getMode());
        root.dataset.navLayout = normalized;
    },

    // --- Secondary settings cluster (WASM: AppTopBar is static, so @onclick never runs) ---
    _secondaryClickBound: false,

    applySecondaryUi: function (expanded) {
        const isExpanded = expanded !== false;
        const cluster = document.querySelector('.app-nav-secondary');
        if (cluster) {
            cluster.style.display = isExpanded ? '' : 'none';
            cluster.setAttribute('data-secondary-expanded', isExpanded ? '1' : '0');
        }

        const btn = document.querySelector('[data-wasm-secondary-toggle]');
        if (!btn) return;

        btn.setAttribute('aria-expanded', isExpanded ? 'true' : 'false');
        btn.title = isExpanded ? 'Hide settings icons' : 'Show settings icons';

        const expIcon = btn.querySelector('.secondary-toggle-icon--expanded');
        const colIcon = btn.querySelector('.secondary-toggle-icon--collapsed');
        if (expIcon) expIcon.style.display = isExpanded ? '' : 'none';
        if (colIcon) colIcon.style.display = isExpanded ? 'none' : '';
    },

    applySecondaryFromStorage: function () {
        this.ensureSecondaryClickDelegation();
        this.applySecondaryUi(this.getPrefs().secondaryExpanded);
    },

    toggleSecondaryExpanded: function () {
        const prefs = this.getPrefs();
        prefs.secondaryExpanded = !prefs.secondaryExpanded;
        this.savePrefs(prefs);
        this.applySecondaryUi(prefs.secondaryExpanded);
        return prefs.secondaryExpanded;
    },

    ensureSecondaryClickDelegation: function () {
        if (this._secondaryClickBound) return;
        const self = this;
        document.addEventListener('click', function (event) {
            const btn = event.target && event.target.closest
                ? event.target.closest('[data-wasm-secondary-toggle]')
                : null;
            if (!btn) return;

            // MAUI uses Blazor @onclick — only handle the WASM marker.
            event.preventDefault();
            event.stopPropagation();
            self.toggleSecondaryExpanded();
        }, true);
        this._secondaryClickBound = true;
    },

    applyEarly: function () {
        try {
            this.apply(this.getMode());
            this.ensureSecondaryClickDelegation();
            // Defer one frame so static AppTopBar markup is present.
            const applyUi = () => {
                try { this.applySecondaryUi(this.getPrefs().secondaryExpanded); } catch (e) { }
            };
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', applyUi, { once: true });
            } else {
                applyUi();
            }
            // Blazor enhanced navigations / WASM body swaps may re-insert the bar.
            setTimeout(applyUi, 0);
            setTimeout(applyUi, 250);
        } catch (e) { }
    }
};

// Bind immediately when the script loads (host shell + WASM).
try {
    window.appNavLayout.ensureSecondaryClickDelegation();
} catch (e) { }
