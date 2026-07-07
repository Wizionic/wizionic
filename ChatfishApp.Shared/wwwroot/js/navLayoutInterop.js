// Navigation bar layout persistence + DOM application (WASM, MAUI WebView, host shell).
window.chatfishNavLayout = window.chatfishNavLayout || {

    storageKey: 'chatfish-nav-layout',

    normalize: function (mode) {
        return mode === 'left' ? 'left' : 'top';
    },

    getMode: function () {
        return this.normalize(localStorage.getItem(this.storageKey));
    },

    saveMode: function (mode) {
        localStorage.setItem(this.storageKey, this.normalize(mode));
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