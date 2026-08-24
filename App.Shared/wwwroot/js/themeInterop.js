// Theme persistence + DOM application for Wizionic (WASM, MAUI WebView, host shell).
// Storage key is namespaced per user: {prefix}app-theme (see setStoragePrefix).
window.appTheme = window.appTheme || {

    baseStorageKey: 'app-theme',
    storagePrefix: '',

    // Dark themes for colorScheme hint
    darkThemes: { 'app-dark': 1, 'dark-blue': 1, 'dracula': 1, 'nord': 1 },

    get storageKey() {
        return (this.storagePrefix || '') + this.baseStorageKey;
    },

    setStoragePrefix: function (prefix) {
        this.storagePrefix = prefix || '';
    },

    getTheme: function () {
        const key = this.storageKey;
        try {
            let value = localStorage.getItem(key);
            if (value !== null)
                return value;

            // Seed from legacy unprefixed key once so existing installs keep their theme.
            if (key !== this.baseStorageKey) {
                const legacy = localStorage.getItem(this.baseStorageKey);
                if (legacy !== null) {
                    localStorage.setItem(key, legacy);
                    return legacy;
                }
            }
        } catch (e) { /* private browsing */ }
        return 'app-light';
    },

    saveTheme: function (theme) {
        try {
            localStorage.setItem(this.storageKey, theme || 'app-light');
        } catch (e) { /* private browsing */ }
    },

    prefersDark: function () {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    resolveEffectiveTheme: function (theme) {
        if (theme === 'system') {
            return this.prefersDark() ? 'app-dark' : 'app-light';
        }
        return theme || 'app-light';
    },

    apply: function (theme) {
        const root = document.documentElement;
        const effective = this.resolveEffectiveTheme(theme);

        // Remove old theme attrs cleanly
        root.removeAttribute('data-theme');
        root.removeAttribute('data-color-scheme');

        if (effective && effective !== 'app-light') {
            root.setAttribute('data-theme', effective);
        }

        root.style.colorScheme = this.darkThemes[effective] ? 'dark' : 'light';
    },

    applyEarly: function () {
        try {
            // Before auth is known, use the signed-out prefix if set; otherwise the unprefixed key.
            const saved = this.getTheme();
            this.apply(saved);
        } catch (e) { }
    },

    initPersistentHooks: function () {
        // Enhanced navigation's 'enhancedload' exists on Blazor Web only.
        // MAUI/WebView Blazor has window.Blazor but no addEventListener — skip cleanly.
        const tryRegister = (attempts) => {
            if (window.Blazor) {
                if (typeof window.Blazor.addEventListener === 'function') {
                    window.Blazor.addEventListener('enhancedload', () => {
                        this.apply(this.getTheme());
                    });
                }
                return;
            }
            if (attempts > 0)
                setTimeout(() => tryRegister(attempts - 1), 100);
        };
        tryRegister(20);

        // MutationObserver — if something strips data-theme from <html>, restore it
        const observer = new MutationObserver(() => {
            const root = document.documentElement;
            const saved = this.getTheme();
            const effective = this.resolveEffectiveTheme(saved);
            const current = root.getAttribute('data-theme') || 'app-light';
            if (current !== effective && !(effective === 'app-light' && !root.hasAttribute('data-theme'))) {
                this.apply(saved);
            }
        });
        observer.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ['data-theme']
        });
    },

    initSystemListener: function (dotnetRef) {
        if (this._systemListener) return;
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        const handler = () => dotnetRef.invokeMethodAsync('OnSystemColorSchemeChanged');
        mq.addEventListener('change', handler);
        this._systemListener = { mq, handler };
    },

    disposeSystemListener: function () {
        if (!this._systemListener) return;
        this._systemListener.mq.removeEventListener('change', this._systemListener.handler);
        this._systemListener = null;
    }
};

window.appPlatform = function () {
    try {
        return (navigator.userAgentData && navigator.userAgentData.platform)
            || navigator.platform
            || navigator.userAgent
            || '';
    } catch (e) {
        return navigator.userAgent || '';
    }
};

// windows | linux | mac | android | ios | ipad | chromeos | unknown
window.appClientOs = function () {
    try {
        var ua = (navigator.userAgent || '').toLowerCase();
        var platform = '';
        try {
            platform = ((navigator.userAgentData && navigator.userAgentData.platform)
                || navigator.platform
                || '').toLowerCase();
        } catch (e) {
            platform = (navigator.platform || '').toLowerCase();
        }
        var touch = navigator.maxTouchPoints || 0;

        if (ua.indexOf('android') >= 0)
            return 'android';
        if (ua.indexOf('iphone') >= 0 || ua.indexOf('ipod') >= 0)
            return 'ios';
        // iPadOS 13+ often reports as Macintosh with touch.
        if (ua.indexOf('ipad') >= 0 || ((platform.indexOf('mac') >= 0 || ua.indexOf('mac') >= 0) && touch > 1))
            return 'ipad';
        if (ua.indexOf('cros') >= 0 || platform.indexOf('cros') >= 0)
            return 'chromeos';
        if (platform.indexOf('win') >= 0 || ua.indexOf('windows') >= 0)
            return 'windows';
        if (platform.indexOf('mac') >= 0 || ua.indexOf('mac') >= 0)
            return 'mac';
        if (platform.indexOf('linux') >= 0 || ua.indexOf('linux') >= 0 || ua.indexOf('x11') >= 0)
            return 'linux';
        return 'unknown';
    } catch (e) {
        return 'unknown';
    }
};
