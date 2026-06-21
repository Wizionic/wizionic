(function () {
    let resizeModuleRegistered = false;

    function registerResizeModule() {
        if (resizeModuleRegistered || typeof Quill === 'undefined') return;
        if (typeof window.QuillResizeImage === 'undefined') {
            console.warn('[QuillFunctions] QuillResizeImage not loaded; image resize disabled.');
            return;
        }

        Quill.register('modules/resize', window.QuillResizeImage);
        resizeModuleRegistered = true;
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
        createQuill: function (editorElement, toolbarElement, readOnly, placeholder, theme) {
            if (!editorElement || typeof Quill === 'undefined') return;

            const existing = getQuillInstance(editorElement);
            if (existing) return;

            registerResizeModule();

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

            new Quill(editorElement, options);
        },

        getQuillHTML: function (editorElement) {
            const quill = getQuillInstance(editorElement);
            return quill ? quill.root.innerHTML : '';
        },

        getQuillText: function (editorElement) {
            const quill = getQuillInstance(editorElement);
            return quill ? quill.getText() : '';
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