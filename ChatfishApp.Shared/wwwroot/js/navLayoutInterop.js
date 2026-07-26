// Navigation bar layout persistence + DOM application (WASM, MAUI WebView, host shell).
// Storage key is namespaced per user: {prefix}chatfish-nav-layout (see setStoragePrefix).
window.chatfishNavLayout = window.chatfishNavLayout || {

    baseStorageKey: 'chatfish-nav-layout',
    storagePrefix: '',

    get storageKey() {
        return (this.storagePrefix || '') + this.baseStorageKey;
    },

    setStoragePrefix: function (prefix) {
        this.storagePrefix = prefix || '';
    },

    normalize: function (mode) {
        return mode === 'left' ? 'left' : 'top';
    },

    getMode: function () {
        const key = this.storageKey;
        try {
            let value = localStorage.getItem(key);
            if (value !== null)
                return this.normalize(value);

            if (key !== this.baseStorageKey) {
                const legacy = localStorage.getItem(this.baseStorageKey);
                if (legacy !== null) {
                    localStorage.setItem(key, legacy);
                    return this.normalize(legacy);
                }
            }
        } catch (e) { /* private browsing */ }
        return 'top';
    },

    saveMode: function (mode) {
        try {
            localStorage.setItem(this.storageKey, this.normalize(mode));
        } catch (e) { /* private browsing */ }
    },

    apply: function (mode) {
        const root = document.documentElement;
        const normalized = this.normalize(mode);
        root.dataset.navLayout = normalized;
    },

    applyEarly: function () {
        try {
            this.apply(this.getMode());
        } catch (e) { }
    }
};
