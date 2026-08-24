// Pointer-based sidebar item reorder (works in browser WASM and MAUI WebView2).
// Used for notebooks and chat history. HTML5 drag-and-drop is unreliable in Blazor Hybrid.
// Items must have data-reorder-id="{id}".
window.appNotesReorder = window.appNotesReorder || {
    start: function (dotNetRef, itemId, startX, startY) {
        if (!dotNetRef || !itemId)
            return;

        const threshold = 6;
        let dragStarted = false;
        let lastOverId = null;

        const getItemAt = (x, y) => {
            const el = document.elementFromPoint(x, y);
            return el?.closest('[data-reorder-id]');
        };

        const onMove = (e) => {
            const clientX = e.clientX ?? (e.touches && e.touches[0]?.clientX);
            const clientY = e.clientY ?? (e.touches && e.touches[0]?.clientY);
            if (clientX == null || clientY == null)
                return;

            if (!dragStarted) {
                const dx = Math.abs(clientX - startX);
                const dy = Math.abs(clientY - startY);
                if (dx < threshold && dy < threshold)
                    return;
                dragStarted = true;
                document.body.style.userSelect = 'none';
                document.body.style.cursor = 'grabbing';
                document.body.classList.add('note-list-reordering');
                try {
                    dotNetRef.invokeMethodAsync('OnReorderDragStarted', itemId);
                } catch (_) { /* circuit may be gone */ }
            }

            if (e.cancelable)
                e.preventDefault();

            const item = getItemAt(clientX, clientY);
            const overId = item?.getAttribute('data-reorder-id') || null;
            if (overId !== lastOverId) {
                lastOverId = overId;
                try {
                    dotNetRef.invokeMethodAsync('OnReorderDragOver', overId);
                } catch (_) { }
            }
        };

        const onUp = (e) => {
            document.removeEventListener('pointermove', onMove, true);
            document.removeEventListener('pointerup', onUp, true);
            document.removeEventListener('pointercancel', onUp, true);
            document.removeEventListener('mousemove', onMove, true);
            document.removeEventListener('mouseup', onUp, true);
            document.removeEventListener('touchmove', onMove, true);
            document.removeEventListener('touchend', onUp, true);
            document.removeEventListener('touchcancel', onUp, true);

            document.body.style.userSelect = '';
            document.body.style.cursor = '';
            document.body.classList.remove('note-list-reordering');

            if (!dragStarted) {
                try {
                    dotNetRef.invokeMethodAsync('OnReorderClick', itemId);
                } catch (_) { }
                return;
            }

            const clientX = e.clientX ?? (e.changedTouches && e.changedTouches[0]?.clientX);
            const clientY = e.clientY ?? (e.changedTouches && e.changedTouches[0]?.clientY);
            let targetId = '';
            if (clientX != null && clientY != null) {
                const item = getItemAt(clientX, clientY);
                targetId = item?.getAttribute('data-reorder-id') || '';
            }

            try {
                dotNetRef.invokeMethodAsync('OnReorderDrop', itemId, targetId || '');
            } catch (_) { }
        };

        document.addEventListener('pointermove', onMove, true);
        document.addEventListener('pointerup', onUp, true);
        document.addEventListener('pointercancel', onUp, true);
        document.addEventListener('mousemove', onMove, true);
        document.addEventListener('mouseup', onUp, true);
        document.addEventListener('touchmove', onMove, { capture: true, passive: false });
        document.addEventListener('touchend', onUp, true);
        document.addEventListener('touchcancel', onUp, true);
    }
};

// Last-selected notebook + Ctrl/Cmd+S while the Notes page is mounted.
window.appNotesUi = window.appNotesUi || (function () {
    const BASE_STORAGE_KEY = 'app-last-notebook-id';

    let storagePrefix = '';
    let saveHandler = null;
    let keydownBound = null;

    function storageKey() {
        return (storagePrefix || '') + BASE_STORAGE_KEY;
    }

    function readStorage(key) {
        try {
            return localStorage.getItem(key);
        } catch (_) {
            return null;
        }
    }

    function writeStorage(key, value) {
        try {
            if (!value)
                localStorage.removeItem(key);
            else
                localStorage.setItem(key, value);
        } catch (_) { /* private browsing */ }
    }

    function setStoragePrefix(prefix) {
        storagePrefix = prefix || '';
    }

    function getLastNotebookId() {
        const key = storageKey();
        let value = readStorage(key);
        if (value)
            return value;

        const fallbacks = [];
        if (key !== BASE_STORAGE_KEY)
            fallbacks.push(BASE_STORAGE_KEY);

        for (const fallback of fallbacks) {
            value = readStorage(fallback);
            if (value) {
                writeStorage(key, value);
                return value;
            }
        }

        return '';
    }

    function setLastNotebookId(id) {
        writeStorage(storageKey(), id || '');
    }

    function onKeyDown(event) {
        if (!(event.ctrlKey || event.metaKey))
            return;
        if (event.key !== 's' && event.key !== 'S')
            return;
        event.preventDefault();
        if (!saveHandler)
            return;
        try {
            saveHandler.invokeMethodAsync('OnNotesSaveHotkey');
        } catch (_) { /* circuit may be gone */ }
    }

    function bindSaveHotkey(dotNetRef) {
        unbindSaveHotkey();
        saveHandler = dotNetRef;
        keydownBound = onKeyDown;
        document.addEventListener('keydown', keydownBound, true);
    }

    function unbindSaveHotkey() {
        if (keydownBound)
            document.removeEventListener('keydown', keydownBound, true);
        keydownBound = null;
        saveHandler = null;
    }

    return {
        setStoragePrefix: setStoragePrefix,
        getLastNotebookId: getLastNotebookId,
        setLastNotebookId: setLastNotebookId,
        bindSaveHotkey: bindSaveHotkey,
        unbindSaveHotkey: unbindSaveHotkey
    };
})();
