window.scrollChatToBottom = function () {
    const container = document.getElementById('chat-container');
    if (container) {
        container.scrollTop = container.scrollHeight;
    }
};

// Used by ChatPage / NotesPage â‹® menus. Also defined in host App.razor for WASM;
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

// Align popup below the â‹® button with right edges flush (message menus).
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

window.setChatTextareaValue = function (el, value) {
    if (!el) return;
    el.value = value == null ? '' : String(value);
    try {
        el.dispatchEvent(new Event('input', { bubbles: true }));
    } catch (_) { /* ignore */ }
};

window.resetChatTextarea = function (el) {
    if (!el) return;
    el.value = '';
};

window.setupChatEnterToSend = function (textareaEl) {
    if (!textareaEl || textareaEl.__appEnterBound) return;
    textareaEl.__appEnterBound = true;
    textareaEl.addEventListener('keydown', function (ev) {
        if (ev.key === 'Enter' && !ev.shiftKey) {
            // Slash skill menu open — Blazor handles Enter (pick suggestion).
            if (document.querySelector('.chat-slash-menu')) return;
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

    if (!window.__appArrayBufferToBase64) {
        window.__appArrayBufferToBase64 = function (buffer) {
            let binary = '';
            const bytes = new Uint8Array(buffer);
            for (let i = 0; i < bytes.byteLength; i++) {
                binary += String.fromCharCode(bytes[i]);
            }
            return window.btoa(binary);
        };
    }

    if (textareaEl.__appPasteBound) return;
    textareaEl.__appPasteBound = true;

    textareaEl.addEventListener('paste', function (ev) {
        let processed = false;
        const dtFiles = ev.clipboardData && ev.clipboardData.files;
        if (dtFiles && dtFiles.length > 0) {
            for (let i = 0; i < dtFiles.length; i++) {
                const file = dtFiles[i];
                if (file && file.type && file.type.indexOf('image/') === 0) {
                    const reader = new FileReader();
                    reader.onload = function (e) {
                        const base64 = window.__appArrayBufferToBase64(e.target.result);
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
                            const base64 = window.__appArrayBufferToBase64(e.target.result);
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
};window.downloadBase64File = function (base64OrDataUrl, fileName, mimeType) {
    try {
        let b64 = base64OrDataUrl || '';
        let mime = mimeType || 'image/png';
        if (b64.indexOf('data:') === 0) {
            const comma = b64.indexOf(',');
            const header = b64.substring(0, comma);
            const m = /data:([^;]+)/.exec(header);
            if (m) mime = m[1];
            b64 = b64.substring(comma + 1);
        }
        const binary = atob(b64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        const blob = new Blob([bytes], { type: mime });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName || 'image.png';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
        return true;
    } catch (e) {
        console.warn('downloadBase64File failed', e);
        return false;
    }
};

window.copyImageFromDataUrl = async function (base64OrDataUrl, mimeType) {
    try {
        let b64 = base64OrDataUrl || '';
        let mime = mimeType || 'image/png';
        if (b64.indexOf('data:') === 0) {
            const comma = b64.indexOf(',');
            const header = b64.substring(0, comma);
            const m = /data:([^;]+)/.exec(header);
            if (m) mime = m[1];
            b64 = b64.substring(comma + 1);
        }
        const binary = atob(b64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        const blob = new Blob([bytes], { type: mime });
        if (navigator.clipboard && window.ClipboardItem) {
            await navigator.clipboard.write([new ClipboardItem({ [mime]: blob })]);
            return 'ok';
        }
        return 'unsupported';
    } catch (e) {
        console.warn('copyImageFromDataUrl failed', e);
        return (e && e.message) ? e.message : 'failed';
    }
};

// --- Lemonade speech-to-text: mic capture â†’ 16 kHz mono PCM â†’ WAV base64 ---
window.__appMic = window.__appMic || { stream: null, ctx: null, processor: null, chunks: [], recording: false };

window.appMicStart = async function () {
    const state = window.__appMic;
    if (state.recording) return { ok: true, already: true };

    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: true
            }
        });
        state.stream = stream;

        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        const ctx = new AudioCtx();
        state.ctx = ctx;
        // Prefer native rate; we resample to 16 kHz on stop for Whisper.
        const source = ctx.createMediaStreamSource(stream);
        // ScriptProcessor is deprecated but widely available in WebView/WASM.
        const bufferSize = 4096;
        const processor = ctx.createScriptProcessor(bufferSize, 1, 1);
        state.processor = processor;
        state.chunks = [];
        state.inputSampleRate = ctx.sampleRate;

        processor.onaudioprocess = function (e) {
            if (!state.recording) return;
            const input = e.inputBuffer.getChannelData(0);
            state.chunks.push(new Float32Array(input));
        };

        source.connect(processor); var silent = ctx.createGain(); silent.gain.value = 0; processor.connect(silent); silent.connect(ctx.destination);
        state.source = source;
        state.recording = true;
        return { ok: true, sampleRate: ctx.sampleRate };
    } catch (e) {
        console.warn('appMicStart failed', e);
        await window.appMicCancel();
        return { ok: false, error: (e && e.message) ? e.message : String(e) };
    }
};

window.appMicCancel = async function () {
    const state = window.__appMic;
    state.recording = false;
    try {
        if (state.processor) {
            state.processor.disconnect();
            state.processor.onaudioprocess = null;
        }
        if (state.source) state.source.disconnect();
        if (state.ctx && state.ctx.state !== 'closed') await state.ctx.close();
        if (state.stream) state.stream.getTracks().forEach(t => t.stop());
    } catch (e) { /* ignore */ }
    state.processor = null;
    state.source = null;
    state.ctx = null;
    state.stream = null;
    state.chunks = [];
};

window.appMicStop = async function () {
    const state = window.__appMic;
    if (!state.recording && (!state.chunks || state.chunks.length === 0)) {
        await window.appMicCancel();
        return { ok: false, error: 'Not recording.' };
    }

    state.recording = false;
    const inputRate = state.inputSampleRate || 48000;
    const chunks = state.chunks || [];

    try {
        if (state.processor) {
            state.processor.disconnect();
            state.processor.onaudioprocess = null;
        }
        if (state.source) state.source.disconnect();
        if (state.ctx && state.ctx.state !== 'closed') await state.ctx.close();
        if (state.stream) state.stream.getTracks().forEach(t => t.stop());
    } catch (e) { /* ignore cleanup */ }

    state.processor = null;
    state.source = null;
    state.ctx = null;
    state.stream = null;
    state.chunks = [];

    if (chunks.length === 0) {
        return { ok: false, error: 'No audio captured. Check microphone permissions.' };
    }

    // Merge float samples
    let total = 0;
    for (let i = 0; i < chunks.length; i++) total += chunks[i].length;
    const merged = new Float32Array(total);
    let offset = 0;
    for (let i = 0; i < chunks.length; i++) {
        merged.set(chunks[i], offset);
        offset += chunks[i].length;
    }

    const targetRate = 16000;
    const resampled = window.__appResampleFloat(merged, inputRate, targetRate);
    const wav = window.__appEncodeWav(resampled, targetRate);
    const b64 = window.__appArrayBufferToBase64(wav.buffer);

    return {
        ok: true,
        base64: b64,
        mimeType: 'audio/wav',
        sampleRate: targetRate,
        durationMs: Math.round((resampled.length / targetRate) * 1000)
    };
};

window.__appArrayBufferToBase64 = window.__appArrayBufferToBase64 || function (buffer) {
    let binary = '';
    const bytes = new Uint8Array(buffer);
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return window.btoa(binary);
};

window.__appResampleFloat = function (input, fromRate, toRate) {
    if (fromRate === toRate) return input;
    const ratio = fromRate / toRate;
    const newLen = Math.max(1, Math.round(input.length / ratio));
    const output = new Float32Array(newLen);
    for (let i = 0; i < newLen; i++) {
        const src = i * ratio;
        const i0 = Math.floor(src);
        const i1 = Math.min(i0 + 1, input.length - 1);
        const t = src - i0;
        output[i] = input[i0] * (1 - t) + input[i1] * t;
    }
    return output;
};

window.__appEncodeWav = function (samples, sampleRate) {
    const numChannels = 1;
    const bitsPerSample = 16;
    const blockAlign = numChannels * bitsPerSample / 8;
    const byteRate = sampleRate * blockAlign;
    const dataSize = samples.length * blockAlign;
    const buffer = new ArrayBuffer(44 + dataSize);
    const view = new DataView(buffer);

    function writeString(offset, str) {
        for (let i = 0; i < str.length; i++) view.setUint8(offset + i, str.charCodeAt(i));
    }

    writeString(0, 'RIFF');
    view.setUint32(4, 36 + dataSize, true);
    writeString(8, 'WAVE');
    writeString(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true); // PCM
    view.setUint16(22, numChannels, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, byteRate, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, bitsPerSample, true);
    writeString(36, 'data');
    view.setUint32(40, dataSize, true);

    let offset = 44;
    for (let i = 0; i < samples.length; i++, offset += 2) {
        let s = Math.max(-1, Math.min(1, samples[i]));
        view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
    }
    return new Uint8Array(buffer);
};

window.appendChatTextareaText = function (el, text) {
    if (!el || typeof text !== 'string') return;
    const cur = (typeof el.value === 'string') ? el.value : '';
    const sep = cur.length > 0 && !/\s$/.test(cur) ? ' ' : '';
    el.value = cur + sep + text;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    try { el.focus(); } catch (e) { }
};

window.appPlayAudioBase64 = async function (base64, mimeType) {
    try {
        let b64 = base64 || '';
        let mime = mimeType || 'audio/mpeg';
        if (b64.indexOf('data:') === 0) {
            const comma = b64.indexOf(',');
            const header = b64.substring(0, comma);
            const m = /data:([^;]+)/.exec(header);
            if (m) mime = m[1];
            b64 = b64.substring(comma + 1);
        }
        const binary = atob(b64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        const blob = new Blob([bytes], { type: mime });
        const url = URL.createObjectURL(blob);
        if (window.__appAudioEl) {
            try { window.__appAudioEl.pause(); } catch (e) {}
            try { URL.revokeObjectURL(window.__appAudioEl.src); } catch (e) {}
        }
        const audio = new Audio(url);
        window.__appAudioEl = audio;
        audio.onended = function () {
            try { URL.revokeObjectURL(url); } catch (e) {}
            if (window.__appAudioEl === audio) window.__appAudioEl = null;
        };
        await audio.play();
        return 'ok';
    } catch (e) {
        console.warn('appPlayAudioBase64 failed', e);
        return (e && e.message) ? e.message : 'failed';
    }
};

window.appStopAudio = function () {
    try {
        if (window.__appAudioEl) {
            window.__appAudioEl.pause();
            try { URL.revokeObjectURL(window.__appAudioEl.src); } catch (e) {}
            window.__appAudioEl = null;
        }
        return true;
    } catch (e) {
        return false;
    }
};

window.appPlayAudioBase64Wait = function (base64, mimeType) {
    return new Promise(async function (resolve) {
        try {
            const play = await window.appPlayAudioBase64(base64, mimeType);
            if (play !== 'ok' || !window.__appAudioEl) {
                resolve(play);
                return;
            }
            const audio = window.__appAudioEl;
            const done = function () {
                audio.onended = null;
                audio.onerror = null;
                resolve('ok');
            };
            audio.addEventListener('ended', done, { once: true });
            audio.addEventListener('error', function () { resolve('playback error'); }, { once: true });
        } catch (e) {
            resolve((e && e.message) ? e.message : 'failed');
        }
    });
};

window.__appVoice = window.__appVoice || {
    stream: null, ctx: null, processor: null, source: null,
    listening: false, muted: false, speaking: false,
    chunks: [], lastLoudAt: 0, speechStartedAt: 0, dotNet: null,
    inputSampleRate: 48000
};

window.appVoiceListenStart = async function (dotNetRef, options) {
    const state = window.__appVoice;
    options = options || {};
    const silenceMs = options.silenceMs || 1100;
    const minSpeechMs = options.minSpeechMs || 350;
    const startRms = options.startRms || 0.02;
    const stopRms = options.stopRms || 0.011;

    if (state.listening) {
        state.dotNet = dotNetRef || state.dotNet;
        return { ok: true, already: true };
    }

    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true }
        });
        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        const ctx = new AudioCtx();
        const source = ctx.createMediaStreamSource(stream);
        const processor = ctx.createScriptProcessor(4096, 1, 1);
        state.stream = stream;
        state.ctx = ctx;
        state.source = source;
        state.processor = processor;
        state.inputSampleRate = ctx.sampleRate;
        state.dotNet = dotNetRef;
        state.listening = true;
        state.muted = false;
        state.speaking = false;
        state.chunks = [];
        state.lastLoudAt = 0;
        state.speechStartedAt = 0;

        processor.onaudioprocess = function (e) {
            if (!state.listening) return;
            const input = e.inputBuffer.getChannelData(0);
            if (state.muted) return;

            let sum = 0;
            for (let i = 0; i < input.length; i++) sum += input[i] * input[i];
            const rms = Math.sqrt(sum / input.length);
            const now = performance.now();

            if (rms >= startRms) {
                if (!state.speaking) {
                    state.speaking = true;
                    state.speechStartedAt = now;
                    state.chunks = [];
                    if (state.dotNet) {
                        try { state.dotNet.invokeMethodAsync('OnVoiceSpeechStart'); } catch (err) { }
                    }
                }
                state.lastLoudAt = now;
                state.chunks.push(new Float32Array(input));
                return;
            }

            if (!state.speaking) return;
            state.chunks.push(new Float32Array(input));
            if (rms >= stopRms)
                state.lastLoudAt = now;

            if (now - state.lastLoudAt < silenceMs) return;

            const durationMs = now - state.speechStartedAt;
            const chunks = state.chunks;
            state.speaking = false;
            state.chunks = [];
            if (durationMs < minSpeechMs || chunks.length === 0) return;
            flushVoiceUtterance(state, chunks, durationMs);
        };

        source.connect(processor);
        var silent = ctx.createGain();
        silent.gain.value = 0;
        processor.connect(silent);
        silent.connect(ctx.destination);
        return { ok: true, sampleRate: ctx.sampleRate };
    } catch (e) {
        console.warn('appVoiceListenStart failed', e);
        await window.appVoiceListenStop();
        return { ok: false, error: (e && e.message) ? e.message : String(e) };
    }
};

function flushVoiceUtterance(state, chunks, durationMs) {
    try {
        let total = 0;
        for (let i = 0; i < chunks.length; i++) total += chunks[i].length;
        const merged = new Float32Array(total);
        let offset = 0;
        for (let i = 0; i < chunks.length; i++) {
            merged.set(chunks[i], offset);
            offset += chunks[i].length;
        }
        const targetRate = 16000;
        const resampled = window.__appResampleFloat(merged, state.inputSampleRate || 48000, targetRate);
        const wav = window.__appEncodeWav(resampled, targetRate);
        const b64 = window.__appArrayBufferToBase64(wav.buffer);
        if (state.dotNet) {
            state.dotNet.invokeMethodAsync('OnVoiceUtterance', b64, Math.round((resampled.length / targetRate) * 1000));
        }
    } catch (e) {
        console.warn('flushVoiceUtterance failed', e);
    }
}

window.appVoiceSetMuted = function (muted) {
    const state = window.__appVoice;
    state.muted = !!muted;
    if (state.muted) {
        state.speaking = false;
        state.chunks = [];
    }
};

window.appVoiceListenStop = async function () {
    const state = window.__appVoice;
    state.listening = false;
    state.speaking = false;
    state.muted = false;
    state.dotNet = null;
    try {
        if (state.processor) {
            state.processor.disconnect();
            state.processor.onaudioprocess = null;
        }
        if (state.source) state.source.disconnect();
        if (state.ctx && state.ctx.state !== 'closed') await state.ctx.close();
        if (state.stream) state.stream.getTracks().forEach(t => t.stop());
    } catch (e) { }
    state.processor = null;
    state.source = null;
    state.ctx = null;
    state.stream = null;
    state.chunks = [];
};
