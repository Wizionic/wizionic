// Pointer-based sidebar item reorder (works in browser WASM and MAUI WebView2).
// Used for notebooks and chat history. HTML5 drag-and-drop is unreliable in Blazor Hybrid.
// Items must have data-reorder-id="{id}".
window.chatfishNotesReorder = window.chatfishNotesReorder || {
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
