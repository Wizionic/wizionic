// Gallery helpers: thumbnail generation, dimensions, blob URLs for display.
(function () {
    function loadImageFromBase64(base64, contentType) {
        return new Promise(function (resolve, reject) {
            var img = new Image();
            img.onload = function () { resolve(img); };
            img.onerror = function () { reject(new Error('Failed to decode image')); };
            var mime = contentType && contentType.indexOf('image/') === 0 ? contentType : 'image/jpeg';
            img.src = 'data:' + mime + ';base64,' + base64;
        });
    }

    function base64ToUint8(b64) {
        var binary = atob(b64);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes;
    }

    /**
     * @returns {{ width: number, height: number, thumbnailBase64: string|null }}
     */
    window.galleryPrepareImage = async function (base64, contentType, maxEdge) {
        maxEdge = maxEdge || 400;
        var img = await loadImageFromBase64(base64, contentType);
        var w = img.naturalWidth || img.width || 0;
        var h = img.naturalHeight || img.height || 0;
        var thumb = null;

        if (w > 0 && h > 0) {
            var scale = Math.min(1, maxEdge / Math.max(w, h));
            var tw = Math.max(1, Math.round(w * scale));
            var th = Math.max(1, Math.round(h * scale));
            try {
                var canvas = document.createElement('canvas');
                canvas.width = tw;
                canvas.height = th;
                var ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0, tw, th);
                var dataUrl = canvas.toDataURL('image/jpeg', 0.72);
                var comma = dataUrl.indexOf(',');
                thumb = comma >= 0 ? dataUrl.substring(comma + 1) : null;
            } catch (e) {
                console.warn('galleryPrepareImage thumbnail failed', e);
            }
        }

        // Drop decoded bitmap ASAP (helps WASM / low-memory browsers).
        try { img.src = ''; } catch (e) { /* ignore */ }

        return { width: w, height: h, thumbnailBase64: thumb };
    };

    /** Build a short-lived blob: URL from base64 (for MAUI or when C# already has bytes). */
    window.galleryObjectUrlFromBase64 = window.galleryObjectUrlFromBase64 || function (b64, contentType) {
        try {
            if (!b64) return null;
            var bytes = base64ToUint8(b64);
            var mime = contentType && contentType.indexOf('image/') === 0 ? contentType : 'image/jpeg';
            return URL.createObjectURL(new Blob([bytes], { type: mime }));
        } catch (e) {
            console.warn('galleryObjectUrlFromBase64 failed', e);
            return null;
        }
    };

    window.galleryRevokeObjectUrl = window.galleryRevokeObjectUrl || function (url) {
        try { if (url) URL.revokeObjectURL(url); } catch (e) { /* ignore */ }
    };
})();
