(function () {
    let resizeModuleRegistered = false;

    let sttBlotRegistered = false;

    function registerResizeModule() {
        if (resizeModuleRegistered || typeof Quill === 'undefined') return;
        if (typeof window.QuillResizeImage === 'undefined') {
            console.warn('[QuillFunctions] QuillResizeImage not loaded; image resize disabled.');
            return;
        }

        Quill.register('modules/resize', window.QuillResizeImage);
        resizeModuleRegistered = true;
    }

    function registerSttBlot() {
        if (sttBlotRegistered || typeof Quill === 'undefined') return;
        const Inline = Quill.import('blots/inline');
        class NoteSttSegBlot extends Inline {
            static blotName = 'noteSttSeg';
            static tagName = 'SPAN';
            static className = 'note-stt-seg';
            static create(value) {
                const node = super.create();
                const v = value && typeof value === 'object' ? value : {};
                if (v.t != null && v.t !== '') node.setAttribute('data-t', String(v.t));
                if (v.audio) node.setAttribute('data-note-audio', String(v.audio));
                return node;
            }
            static formats(node) {
                return {
                    t: node.getAttribute('data-t') || '',
                    audio: node.getAttribute('data-note-audio') || ''
                };
            }
        }
        Quill.register(NoteSttSegBlot, true);
        sttBlotRegistered = true;
    }

    function getQuillInstance(element) {
        if (!element) return null;
        if (typeof Quill !== 'undefined' && typeof Quill.find === 'function') {
            const found = Quill.find(element);
            if (found) return found;
        }
        return element.__quill || null;
    }

    window.QuillFunctions = {
        createQuill: function (editorElement, toolbarElement, readOnly, placeholder, theme, dotNetHelper, textChangeMethod) {
            if (!editorElement || typeof Quill === 'undefined') return;

            const existing = getQuillInstance(editorElement);
            if (existing) return;

            registerResizeModule();
            registerSttBlot();

            const modules = { toolbar: toolbarElement };
            if (resizeModuleRegistered) {
                modules.resize = {
                    locale: {
                        floatLeft: 'Float left',
                        floatRight: 'Float right',
                        center: 'Center',
                        restore: 'Restore',
                        altTip: 'Hold Alt to lock aspect ratio',
                        inputTip: 'Press Enter to apply'
                    }
                };
            }

            const options = {
                modules: modules,
                placeholder: placeholder || 'Insert text here...',
                readOnly: !!readOnly,
                theme: theme || 'snow'
            };

            const quill = new Quill(editorElement, options);

            quill.clipboard.addMatcher('SPAN', function (node, delta) {
                if (!node.classList || !node.classList.contains('note-stt-seg'))
                    return delta;
                const t = node.getAttribute('data-t') || '';
                const audio = node.getAttribute('data-note-audio') || '';
                delta.ops.forEach(function (op) {
                    if (typeof op.insert === 'string') {
                        op.attributes = op.attributes || {};
                        op.attributes.noteSttSeg = { t: t, audio: audio };
                    }
                });
                return delta;
            });

            if (dotNetHelper && textChangeMethod) {
                quill.on('text-change', function () {
                    dotNetHelper.invokeMethodAsync(textChangeMethod, quill.root.innerHTML);
                });
            }
        },

        getQuillHTML: function (editorElement) {
            const quill = getQuillInstance(editorElement);
            return quill ? quill.root.innerHTML : '';
        },

        getQuillText: function (editorElement) {
            const quill = getQuillInstance(editorElement);
            return quill ? quill.getText() : '';
        },

        insertText: function (editorElement, text) {
            const quill = getQuillInstance(editorElement);
            if (!quill || text == null || text === '') return;
            const range = quill.getSelection(true);
            const index = range ? range.index : Math.max(0, quill.getLength() - 1);
            quill.insertText(index, text, 'user');
            quill.setSelection(index + String(text).length, 0, 'user');
        },

        insertHtml: function (editorElement, html) {
            const quill = getQuillInstance(editorElement);
            if (!quill || !html) return;
            const range = quill.getSelection(true);
            const index = range ? range.index : Math.max(0, quill.getLength() - 1);
            quill.clipboard.dangerouslyPasteHTML(index, html, 'user');
        },

        insertSttSeg: function (editorElement, text, startSeconds, audioId, newParagraph) {
            const quill = getQuillInstance(editorElement);
            if (!quill || text == null || text === '') return;
            const range = quill.getSelection(true);
            let index = range ? range.index : Math.max(0, quill.getLength() - 1);
            if (newParagraph) {
                quill.insertText(index, '\n\n', 'user');
                index += 2;
            }
            const payload = { t: String(startSeconds ?? 0), audio: audioId || '' };
            const insert = String(text) + ' ';
            quill.insertText(index, insert, { noteSttSeg: payload }, 'user');
            quill.setSelection(index + insert.length, 0, 'user');
        },

        getSttCueAtCursor: function (editorElement) {
            const quill = getQuillInstance(editorElement);
            if (!quill) return null;
            const range = quill.getSelection(true);
            let index = range ? range.index : 0;
            for (let i = index; i >= 0; i--) {
                const f = quill.getFormat(Math.max(0, i), 1);
                if (f && f.noteSttSeg) {
                    const v = f.noteSttSeg;
                    const t = parseFloat(v && v.t != null ? v.t : v);
                    if (!isNaN(t))
                        return { t: t, audio: (v && v.audio) || '' };
                }
            }
            return null;
        },

        destroyQuill: function (editorElement) {
            const quill = getQuillInstance(editorElement);
            if (!quill) return;

            if (typeof quill.off === 'function') {
                quill.off('text-change');
            }
            if (typeof quill.disable === 'function') {
                quill.disable();
            }

            delete editorElement.__quill;
        }
    };
})();
