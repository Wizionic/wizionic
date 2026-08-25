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
    _accountMenuBound: false,

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
        const cluster = document.querySelector('.app-nav-secondary');
        const currentlyOpen = !!(cluster && cluster.getAttribute('data-secondary-expanded') === '1'
            && cluster.style.display !== 'none');
        const prefs = this.getPrefs();
        prefs.secondaryExpanded = !currentlyOpen;
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

    setAccountMenuOpen: function (open) {
        const isOpen = open === true;
        const menu = document.querySelector('[data-account-menu]');
        const btn = document.querySelector('[data-wasm-account-menu]');
        if (menu) {
            menu.classList.toggle('is-open', isOpen);
            if (isOpen)
                menu.removeAttribute('hidden');
            else
                menu.setAttribute('hidden', '');
        }
        if (btn)
            btn.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    },

    ensureAccountMenuDelegation: function () {
        if (this._accountMenuBound) return;
        const self = this;
        document.addEventListener('click', function (event) {
            const t = event.target;
            if (!t || !t.closest) return;

            const btn = t.closest('[data-wasm-account-menu]');
            const menu = document.querySelector('[data-account-menu]');
            if (btn) {
                event.preventDefault();
                event.stopPropagation();
                const open = !(menu && menu.classList.contains('is-open'));
                self.setAccountMenuOpen(open);
                return;
            }

            if (menu && menu.classList.contains('is-open')) {
                if (menu.contains(t) && t.closest('a'))
                    self.setAccountMenuOpen(false);
                else if (!menu.contains(t))
                    self.setAccountMenuOpen(false);
            }
        }, true);
        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape')
                self.setAccountMenuOpen(false);
        });
        window.addEventListener('resize', function () {
            self.setAccountMenuOpen(false);
        });
        this._accountMenuBound = true;
    },

    applyEarly: function () {
        try {
            this.apply(this.getMode());
            this.ensureSecondaryClickDelegation();
            this.ensureAccountMenuDelegation();
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
    window.appNavLayout.ensureAccountMenuDelegation();
} catch (e) { }

window.appHelp = window.appHelp || {};
window.appHelp.scrollToId = function (id) {
    if (!id) return false;
    var el = document.getElementById(id);
    if (!el) {
        try { el = document.querySelector('#' + CSS.escape(id)); } catch (e) { }
    }
    if (!el) return false;

    var section = el.closest('.help-section');
    if (section) {
        section.classList.add('is-target');
        if (section.__helpTargetTimer) clearTimeout(section.__helpTargetTimer);
        section.__helpTargetTimer = setTimeout(function () {
            section.classList.remove('is-target');
        }, 1800);
    }

    var root = el.closest('.help-view-article') || el.closest('.help-modal-body') || el;
    while (root && root !== document.body) {
        var style = window.getComputedStyle(root);
        var oy = style.overflowY;
        if ((oy === 'auto' || oy === 'scroll' || oy === 'overlay') && root.scrollHeight > root.clientHeight + 2) {
            var top = el.getBoundingClientRect().top - root.getBoundingClientRect().top + root.scrollTop - 12;
            root.scrollTo({ top: Math.max(0, top), behavior: 'smooth' });
            return true;
        }
        root = root.parentElement;
    }
    el.scrollIntoView({ block: 'start', behavior: 'smooth' });
    return true;
};
