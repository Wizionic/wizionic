// Theme persistence + DOM application for Chatfish (WASM, MAUI WebView, host shell).
window.chatfishTheme = window.chatfishTheme || {
    storageKeys: {
        colorScheme: 'chatfish-color-scheme',
        theme: 'chatfish-theme'
    },

    getColorScheme: function () {
        try {
            return localStorage.getItem(this.storageKeys.colorScheme) || 'system';
        } catch (e) {
            return 'system';
        }
    },

    getTheme: function () {
        try {
            return localStorage.getItem(this.storageKeys.theme) || 'default';
        } catch (e) {
            return 'default';
        }
    },

    getSaved: function () {
        return {
            colorScheme: this.getColorScheme(),
            theme: this.getTheme()
        };
    },

    save: function (colorScheme, theme) {
        localStorage.setItem(this.storageKeys.colorScheme, colorScheme || 'system');
        localStorage.setItem(this.storageKeys.theme, theme || 'default');
    },

    prefersDark: function () {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    resolveEffectiveTheme: function (colorScheme, theme) {
        if (theme && theme !== 'default') {
            return theme;
        }
        const dark = colorScheme === 'dark' ||
            (colorScheme === 'system' && this.prefersDark());
        return dark ? 'dark' : 'default';
    },

    apply: function (colorScheme, theme) {
        const root = document.documentElement;
        root.setAttribute('data-color-scheme', colorScheme || 'system');

        const effective = this.resolveEffectiveTheme(colorScheme, theme);
        if (!effective || effective === 'default') {
            root.removeAttribute('data-theme');
        } else {
            root.setAttribute('data-theme', effective);
        }

        const darkThemes = { dark: 1, dracula: 1, nord: 1 };
        root.style.colorScheme = darkThemes[effective] ? 'dark' : 'light';
    },

    applySaved: function () {
        const saved = this.getSaved();
        this.apply(saved.colorScheme, saved.theme);
        return saved;
    },

    applyEarly: function () {
        try {
            this.applySaved();
        } catch (e) {
            // localStorage may be unavailable during SSR or restricted contexts.
        }
    },

    _expectedEffectiveTheme: function () {
        const saved = this.getSaved();
        return this.resolveEffectiveTheme(saved.colorScheme, saved.theme);
    },

    _currentEffectiveTheme: function () {
        return document.documentElement.getAttribute('data-theme') || 'default';
    },

    ensureThemeAttribute: function () {
        const expected = this._expectedEffectiveTheme();
        const current = this._currentEffectiveTheme();
        if (current !== expected) {
            this.applySaved();
        }
    },

    _scheduleReapply: function () {
        const reapply = () => this.applySaved();
        reapply();
        setTimeout(reapply, 0);
        setTimeout(reapply, 50);
        setTimeout(reapply, 150);
        setTimeout(reapply, 350);
    },

    _attachBlazorEnhancedHook: function () {
        if (this._blazorHookAttached) {
            return true;
        }
        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            window.Blazor.addEventListener('enhancedload', () => this._scheduleReapply());
            this._blazorHookAttached = true;
            return true;
        }
        return false;
    },

    initPersistentHooks: function () {
        if (this._persistentHooks) {
            this._attachBlazorEnhancedHook();
            return;
        }
        this._persistentHooks = true;

        const self = this;

        // Blazor Web App enhanced navigation can run before this script; retry until Blazor exists.
        if (!this._attachBlazorEnhancedHook()) {
            const timer = setInterval(function () {
                if (self._attachBlazorEnhancedHook()) {
                    clearInterval(timer);
                }
            }, 25);
            setTimeout(function () { clearInterval(timer); }, 30000);
        }

        // Top-bar and in-app <a href="/..."> navigations (capture phase).
        if (!this._clickHookAttached) {
            this._clickHookAttached = true;
            document.addEventListener('click', function (e) {
                const link = e.target && e.target.closest ? e.target.closest('a[href]') : null;
                if (!link) {
                    return;
                }
                const href = link.getAttribute('href');
                if (!href || href.charAt(0) !== '/' || href.indexOf('//') === 0) {
                    return;
                }
                self._scheduleReapply();
            }, true);
        }

        // Enhanced navigation may strip data-theme from <html>; restore when it drifts.
        if (!this._attrObserver) {
            this._attrObserver = new MutationObserver(function () {
                self.ensureThemeAttribute();
            });
            this._attrObserver.observe(document.documentElement, {
                attributes: true,
                attributeFilter: ['data-theme', 'data-color-scheme']
            });
        }

        // System color scheme changes when user chose "system".
        if (!this._systemListener) {
            const mq = window.matchMedia('(prefers-color-scheme: dark)');
            const handler = function () {
                if (self.getColorScheme() === 'system') {
                    self.applySaved();
                }
            };
            mq.addEventListener('change', handler);
            this._systemListener = { mq: mq, handler: handler };
        }
    },

    initSystemListener: function (dotnetRef) {
        if (this._dotnetSystemListener) {
            return;
        }
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        const handler = function () {
            dotnetRef.invokeMethodAsync('OnSystemColorSchemeChanged');
        };
        mq.addEventListener('change', handler);
        this._dotnetSystemListener = { mq: mq, handler: handler };
    },

    disposeSystemListener: function () {
        if (!this._dotnetSystemListener) {
            return;
        }
        this._dotnetSystemListener.mq.removeEventListener('change', this._dotnetSystemListener.handler);
        this._dotnetSystemListener = null;
    }
};