// PDF.js 5.4.624 is distributed locally (vendor/pdfjs/LICENSE).
// Keep the module and worker on .js URLs: hybrid WebViews may serve .mjs as binary.
import { getDocument, GlobalWorkerOptions } from './vendor/pdfjs/pdf.min.js';
const base = new URL('./vendor/pdfjs/', import.meta.url).href;
GlobalWorkerOptions.workerSrc = base + 'pdf.worker.min.js';
function open(bytes) {
    return getDocument({data:bytes, isEvalSupported:false, useSystemFonts:true,
        cMapUrl:base+'cmaps/', cMapPacked:true, standardFontDataUrl:base+'standard_fonts/', wasmUrl:base+'wasm/'});
}
export async function pages(bytes) {
    const task = open(bytes);
    try {
        const pdf = await task.promise, text = []; let remaining = 2_000_000;
        for (let i=1; i<=pdf.numPages && remaining>0; i++) {
            const page = await pdf.getPage(i), content = await page.getTextContent();
            const s = content.items.map(x => x.str ?? '').join(' ').slice(0, remaining);
            text.push(s); remaining -= s.length; page.cleanup();
        }
        return text;
    } finally { await task.destroy(); }
}
const viewers = new WeakMap();
export async function display(canvas, bytes) {
    await close(canvas);
    const task=open(bytes); viewers.set(canvas, {task});
    return (await task.promise).numPages;
}
export async function draw(canvas, number, zoom) {
    const state=viewers.get(canvas); if (!state) return;
    state.render?.cancel(); const prev=state.render; if(prev) await prev.promise.catch(()=>{});
    const pdf=await state.task.promise, page=await pdf.getPage(number);
    if(viewers.get(canvas)!==state) return;
    const view=page.getViewport({scale:1});
    const scale=Math.min(3,Math.max(.25,(canvas.parentElement.clientWidth-18)/view.width)*zoom);
    const v=page.getViewport({scale}); canvas.width=v.width; canvas.height=v.height;
    state.render=page.render({canvasContext:canvas.getContext('2d'),viewport:v});
    try { await state.render.promise; } catch(e) { if(e.name!=='RenderingCancelledException') throw e; }
}
export async function close(canvas) { const s=viewers.get(canvas); viewers.delete(canvas); if(s){s.render?.cancel();await s.task.destroy();} }
