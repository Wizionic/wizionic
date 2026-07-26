// Theme persistence + DOM application for Chatfish (WASM, MAUI WebView, host shell).
// Storage key is namespaced per user: {prefix}chatfish-theme (see setStoragePrefix).
window.chatfishTheme = window.chatfishTheme || {

    baseStorageKey: 'chatfish-theme',
    storagePrefix: '',

    // Dark themes for colorScheme hint
    darkThemes: { 'chatfish-dark': 1, 'dracula': 1, 'nord': 1 },

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
        return 'system';
    },

    saveTheme: function (theme) {
        try {
            localStorage.setItem(this.storageKey, theme || 'system');
        } catch (e) { /* private browsing */ }
    },

    prefersDark: function () {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    resolveEffectiveTheme: function (theme) {
        if (theme === 'system') {
            return this.prefersDark() ? 'chatfish-dark' : 'chatfish-light';
        }
        return theme || 'chatfish-light';
    },

    apply: function (theme) {
        const root = document.documentElement;
        const effective = this.resolveEffectiveTheme(theme);

        // Remove old theme attrs cleanly
        root.removeAttribute('data-theme');
        root.removeAttribute('data-color-scheme');

        if (effective && effective !== 'chatfish-light') {
            root.setAttribute('data-theme', effective);
        }

        root.style.colorScheme = this.darkThemes[effective] ? 'dark' : 'light';
    },

    applyEarly: function () {
        try {
            // Before auth is known, use guest prefix if set; otherwise legacy/unprefixed key.
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
            const current = root.getAttribute('data-theme') || 'chatfish-light';
            if (current !== effective && !(effective === 'chatfish-light' && !root.hasAttribute('data-theme'))) {
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
