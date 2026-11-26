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
            showDocuments: true,
            // Settings cluster collapsed until the user expands it.
            secondaryExpanded: false,
            // Nav bar hides until edge hover / focus / touch handle.
            autoHide: false
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
            showDocuments: raw.showDocuments !== false,
            // Default closed: only true when explicitly saved as true.
            secondaryExpanded: raw.secondaryExpanded === true,
            autoHide: raw.autoHide === true
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

    apply: function (mode, autoHide) {
        const root = document.documentElement;
        const normalized = this.normalizeMode(mode != null ? mode : this.getMode());
        root.dataset.navLayout = normalized;
        this.applyAutoHide(autoHide != null ? autoHide : this.getPrefs().autoHide);
    },

    // --- Auto-hide navigation (top bar or left rail) ---
    _autoHidePinned: false,
    _revealTimer: null,
    _hideTimer: null,
    _autoHideChromeBound: false,
    _navHandle: null,
    _activateHideTimer: null,
    _ignoreFocusPin: false,

    applyAutoHide: function (on) {
        const root = document.documentElement;
        const enabled = on === true;
        if (enabled) {
            root.setAttribute('data-nav-autohide', '1');
            this.ensureAutoHideChrome();
        } else {
            root.removeAttribute('data-nav-autohide');
            this.hideNav();
        }
        if (this._navHandle)
            this._navHandle.textContent = root.dataset.navLayout === 'left' ? '›' : '⌄';
    },

    revealNav: function (pinned) {
        this._autoHidePinned = pinned === true;
        if (this._hideTimer) { clearTimeout(this._hideTimer); this._hideTimer = null; }
        document.documentElement.setAttribute('data-nav-revealed', '1');
    },

    hideNav: function () {
        this._autoHidePinned = false;
        this._ignoreFocusPin = true;
        if (this._revealTimer) { clearTimeout(this._revealTimer); this._revealTimer = null; }
        if (this._hideTimer) { clearTimeout(this._hideTimer); this._hideTimer = null; }
        if (this._activateHideTimer) { clearTimeout(this._activateHideTimer); this._activateHideTimer = null; }
        document.documentElement.removeAttribute('data-nav-revealed');
        // Ignore the focus event that follows a click on a nav button — otherwise
        // focusin would immediately re-pin the bar we just hid.
        const self = this;
        setTimeout(() => { self._ignoreFocusPin = false; }, 120);
    },

    // Hide after activating a nav destination (link or panel button). Popup
    // triggers (account menu, settings chevron) keep the bar visible.
    scheduleHideAfterActivation: function () {
        if (!this._activateHideTimer) {
            const self = this;
            this._activateHideTimer = setTimeout(() => {
                self._activateHideTimer = null;
                self.hideNav();
            }, 60);
        }
    },

    navEl: function () {
        return document.querySelector('.app-nav.top-row');
    },

    ensureAutoHideChrome: function () {
        if (this._autoHideChromeBound) return;
        if (!document.body) return;
        const self = this;

        const hotzone = document.createElement('div');
        hotzone.className = 'app-nav-hotzone';
        hotzone.setAttribute('aria-hidden', 'true');
        document.body.appendChild(hotzone);

        const handle = document.createElement('button');
        handle.type = 'button';
        handle.className = 'app-nav-handle';
        handle.title = 'Show navigation';
        handle.setAttribute('aria-label', 'Show navigation');
        handle.textContent = '⌄';
        document.body.appendChild(handle);
        this._navHandle = handle;

        const autoHideOn = () => document.documentElement.getAttribute('data-nav-autohide') === '1';

        hotzone.addEventListener('pointerenter', () => {
            if (!autoHideOn()) return;
            if (self._revealTimer) clearTimeout(self._revealTimer);
            self._revealTimer = setTimeout(() => self.revealNav(false), 150);
        });
        hotzone.addEventListener('pointerleave', () => {
            if (self._revealTimer) { clearTimeout(self._revealTimer); self._revealTimer = null; }
        });

        handle.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (document.documentElement.getAttribute('data-nav-revealed') === '1')
                self.hideNav();
            else
                self.revealNav(true);
        });

        // Keep the nav open while the pointer is inside it; hide shortly after it leaves.
        document.addEventListener('pointerover', (e) => {
            if (!autoHideOn()) return;
            const nav = self.navEl();
            const t = e.target;
            if (!nav || !t || !t.closest) return;
            if (nav.contains(t) || hotzone.contains(t)) {
                if (self._hideTimer) { clearTimeout(self._hideTimer); self._hideTimer = null; }
            }
        });
        document.addEventListener('pointerout', (e) => {
            if (!autoHideOn() || self._autoHidePinned) return;
            const nav = self.navEl();
            const t = e.target;
            if (!nav || !t || !t.closest || !nav.contains(t)) return;
            const to = e.relatedTarget;
            if (to && (nav.contains(to) || hotzone.contains(to))) return;
            if (self._hideTimer) clearTimeout(self._hideTimer);
            self._hideTimer = setTimeout(() => self.hideNav(), 250);
        });

        // Keyboard access: focusing any nav control reveals it (unless we just
        // hid the bar after activating a nav button).
        document.addEventListener('focusin', (e) => {
            if (!autoHideOn() || self._ignoreFocusPin) return;
            const nav = self.navEl();
            if (nav && nav.contains(e.target)) self.revealNav(true);
        });
        document.addEventListener('focusout', (e) => {
            if (!autoHideOn() || !self._autoHidePinned) return;
            const nav = self.navEl();
            const to = e.relatedTarget;
            if (nav && (!to || !nav.contains(to))) self.hideNav();
        });

        // Touch: tap outside closes a pinned nav. Escape always closes.
        document.addEventListener('pointerdown', (e) => {
            if (!autoHideOn() || !self._autoHidePinned) return;
            const nav = self.navEl();
            const t = e.target;
            if (nav && t && !nav.contains(t) && t !== handle && !handle.contains(t))
                self.hideNav();
        }, true);
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') self.hideNav();
        });

        // Navigating / switching panels collapses the nav again — for links AND
        // buttons (MAUI uses buttons for Chat/Notes/etc.). Popup triggers stay open.
        document.addEventListener('click', (e) => {
            if (!autoHideOn()) return;
            const nav = self.navEl();
            const t = e.target;
            if (!nav || !t || !t.closest || !nav.contains(t)) return;
            const control = t.closest('a, button');
            if (!control) return;
            if (control.matches('[data-wasm-account-menu], [data-wasm-secondary-toggle], .app-nav-account-menu-btn, .app-nav-secondary-toggle'))
                return;
            self.scheduleHideAfterActivation();
        }, true);

        this._autoHideChromeBound = true;
    },

    // --- WASM right-click menu on the nav bar (MAUI uses the Blazor context menu) ---
    _navContextMenuBound: false,
    _navContextMenuEl: null,

    closeNavContextMenu: function () {
        if (this._navContextMenuEl) {
            this._navContextMenuEl.remove();
            this._navContextMenuEl = null;
        }
    },

    ensureNavContextMenuDelegation: function () {
        if (this._navContextMenuBound) return;
        const self = this;

        document.addEventListener('contextmenu', (e) => {
            const t = e.target;
            const bar = t && t.closest ? t.closest('[data-wasm-nav-contextmenu]') : null;
            if (!bar) return;
            e.preventDefault();
            self.closeNavContextMenu();

            const prefs = self.getPrefs();
            const menu = document.createElement('div');
            menu.className = 'app-nav-context-menu';
            menu.style.position = 'fixed';
            menu.style.zIndex = '10001';

            const section = document.createElement('div');
            section.className = 'app-nav-context-menu-section';
            section.textContent = 'Navigation';
            menu.appendChild(section);

            const item = document.createElement('button');
            item.type = 'button';
            item.className = 'app-nav-context-menu-item' + (prefs.autoHide ? ' is-selected' : '');
            item.textContent = 'Auto-hide navigation bar';
            item.addEventListener('click', () => {
                const next = !self.getPrefs().autoHide;
                const p = self.getPrefs();
                p.autoHide = next;
                self.savePrefs(p);
                self.applyAutoHide(next);
                self.closeNavContextMenu();
            });
            menu.appendChild(item);

            document.body.appendChild(menu);
            const rect = menu.getBoundingClientRect();
            let x = e.clientX, y = e.clientY;
            if (x + rect.width > window.innerWidth - 8) x = window.innerWidth - rect.width - 8;
            if (y + rect.height > window.innerHeight - 8) y = window.innerHeight - rect.height - 8;
            menu.style.left = Math.max(8, x) + 'px';
            menu.style.top = Math.max(8, y) + 'px';
            self._navContextMenuEl = menu;
        });

        document.addEventListener('click', (e) => {
            if (self._navContextMenuEl && !self._navContextMenuEl.contains(e.target))
                self.closeNavContextMenu();
        }, true);
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') self.closeNavContextMenu();
        });
        window.addEventListener('resize', () => self.closeNavContextMenu());
        window.addEventListener('scroll', () => self.closeNavContextMenu(), true);

        this._navContextMenuBound = true;
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
            this.ensureNavContextMenuDelegation();
            // Defer one frame so static AppTopBar markup is present.
            const applyUi = () => {
                try {
                    this.applySecondaryUi(this.getPrefs().secondaryExpanded);
                    this.applyAutoHide(this.getPrefs().autoHide);
                } catch (e) { }
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
    window.appNavLayout.ensureNavContextMenuDelegation();
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.appNavLayout.applyAutoHide(window.appNavLayout.getPrefs().autoHide), { once: true });
    } else {
        window.appNavLayout.applyAutoHide(window.appNavLayout.getPrefs().autoHide);
    }
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
