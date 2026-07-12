window.scrollChatToBottom = function () {
    const container = document.getElementById('chat-container');
    if (container) {
        container.scrollTop = container.scrollHeight;
    }
};

// Used by ChatPage / NotesPage ⋮ menus. Also defined in host App.razor for WASM;
// MAUI loads this file only, so it must be available here too.
window.getMenuPopupPosition = function (buttonId) {
    const btn = document.getElementById(buttonId);
    if (!btn) return { top: 120, left: 260 };
    const rect = btn.getBoundingClientRect();
    const popupWidth = 120;
    let left = rect.right + 5;
    if (left + popupWidth > window.innerWidth) {
        left = rect.left - popupWidth - 5;
    }
    return {
        top: rect.bottom + 2,
        left: left
    };
};

// Align popup below the ⋮ button with right edges flush (message menus).
window.getBotMenuPopupPosition = function (buttonId) {
    const btn = document.getElementById(buttonId);
    if (!btn) return { top: 120, left: 260 };
    const rect = btn.getBoundingClientRect();
    const popupWidth = 160;
    let left = rect.right - popupWidth;
    if (left < 8) left = 8;
    return {
        top: rect.bottom + 2,
        left: left
    };
};

window.isMobileViewport = function () {
    return window.innerWidth <= 640.98;
};

window.getChatTextareaValue = function (el) {
    return (el && typeof el.value === 'string') ? el.value : '';
};

window.resetChatTextarea = function (el) {
    if (!el) return;
    el.value = '';
};

window.setupChatEnterToSend = function (textareaEl) {
    if (!textareaEl || textareaEl.__chatfishEnterBound) return;
    textareaEl.__chatfishEnterBound = true;
    textareaEl.addEventListener('keydown', function (ev) {
        if (ev.key === 'Enter' && !ev.shiftKey) {
            ev.preventDefault();
            const btn = document.getElementById('chat-send-btn');
            if (btn) btn.click();
        }
    });
};

window.initWasmSidebarForViewport = function () {
    if (!window.isMobileViewport()) return false;
    window.toggleWasmSidebar(true);
    return true;
};

window.toggleWasmSidebar = function (collapsed) {
    const pageEl = document.querySelector('.page');
    const sidebar = document.querySelector('.sidebar');
    const mainEl = document.querySelector('main');
    if (pageEl) {
        if (collapsed) {
            pageEl.classList.add('sidebar-collapsed');
        } else {
            pageEl.classList.remove('sidebar-collapsed');
        }
    }
    if (sidebar) sidebar.style.display = '';
    if (mainEl) mainEl.style.marginLeft = '';
};

window.setupChatImagePaste = function (dotnetHelper, textareaEl) {
    if (!textareaEl || !dotnetHelper) return;

    if (!window.__chatfishArrayBufferToBase64) {
        window.__chatfishArrayBufferToBase64 = function (buffer) {
            let binary = '';
            const bytes = new Uint8Array(buffer);
            for (let i = 0; i < bytes.byteLength; i++) {
                binary += String.fromCharCode(bytes[i]);
            }
            return window.btoa(binary);
        };
    }

    if (textareaEl.__chatfishPasteBound) return;
    textareaEl.__chatfishPasteBound = true;

    textareaEl.addEventListener('paste', function (ev) {
        let processed = false;
        const dtFiles = ev.clipboardData && ev.clipboardData.files;
        if (dtFiles && dtFiles.length > 0) {
            for (let i = 0; i < dtFiles.length; i++) {
                const file = dtFiles[i];
                if (file && file.type && file.type.indexOf('image/') === 0) {
                    const reader = new FileReader();
                    reader.onload = function (e) {
                        const base64 = window.__chatfishArrayBufferToBase64(e.target.result);
                        const ext = (file.type.split('/')[1] || 'png');
                        const fileName = file.name || ('pasted-image-' + Date.now() + '.' + ext);
                        dotnetHelper.invokeMethodAsync('OnImagePasted', base64, file.type, fileName, file.size || 0);
                    };
                    reader.readAsArrayBuffer(file);
                    ev.preventDefault();
                    processed = true;
                    break;
                }
            }
        }
        if (!processed) {
            const items = ev.clipboardData && ev.clipboardData.items;
            if (items) {
                for (let i = 0; i < items.length; i++) {
                    const item = items[i];
                    if (item.type && item.type.indexOf('image/') === 0) {
                        const file = item.getAsFile();
                        if (!file) continue;
                        const reader = new FileReader();
                        reader.onload = function (e) {
                            const base64 = window.__chatfishArrayBufferToBase64(e.target.result);
                            const ext = (file.type.split('/')[1] || 'png');
                            const fileName = file.name || ('pasted-image-' + Date.now() + '.' + ext);
                            dotnetHelper.invokeMethodAsync('OnImagePasted', base64, file.type, fileName, file.size || 0);
                        };
                        reader.readAsArrayBuffer(file);
                        ev.preventDefault();
                        break;
                    }
                }
            }
        }
    });
};