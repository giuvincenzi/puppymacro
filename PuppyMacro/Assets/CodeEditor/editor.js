// The macro editor's Code view, inside a WebView2 (Views/MacroCodeView.xaml.cs).
// Messages from the app: init { text, generation, schema, dark }, setText { text, generation }, format, reveal { line, column },
// appMarkers { markers: [{ line, column, message }] }, theme { dark }, focus.
// Messages to the app: ready, changed { text, generation, version, markers: [{ line, column, message }] }, cursor { line, column }.
// generation: which text the app last set, so it can ignore reports about older text.
(function () {
    'use strict';

    const host = window.chrome.webview;
    const APP_OWNER = 'puppymacro'; // markers set by the app (MacroJson checks)
    let editor = null;
    let model = null;
    let changeTimer = 0;
    let lastSent = '';
    let generation = 0;

    function post(message) {
        host.postMessage(message);
    }

    // Monaco's own markers: JSON syntax and the schema (not the app's, which it sends itself).
    function editorMarkers() {
        return monaco.editor.getModelMarkers({ resource: model.uri })
            .filter(m => m.owner !== APP_OWNER && m.severity >= monaco.MarkerSeverity.Warning)
            .map(m => ({ line: m.startLineNumber, column: m.startColumn, message: m.message }));
    }

    function sendChanged() {
        changeTimer = 0;
        const message = { type: 'changed', text: model.getValue(), generation: generation, version: model.getVersionId(), markers: editorMarkers() };
        const key = generation + '|' + message.version + '|' + JSON.stringify(message.markers);
        if (key === lastSent)
            return; // the app's own markers changed: nothing new to report
        lastSent = key;
        post(message);
    }

    function scheduleChanged() {
        if (changeTimer)
            clearTimeout(changeTimer);
        changeTimer = setTimeout(sendChanged, 200);
    }

    function init(message) {
        generation = message.generation;
        monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
            validate: true,
            allowComments: false,
            trailingCommas: 'error',
            schemaValidation: 'error',
            enableSchemaRequest: false,
            schemas: [{ uri: 'https://puppymacro.editor/macro.schema.json', fileMatch: ['*'], schema: message.schema }],
        });
        model = monaco.editor.createModel(message.text, 'json', monaco.Uri.parse('inmemory://puppymacro/macro.json'));
        model.updateOptions({ tabSize: 2, insertSpaces: true });
        editor = monaco.editor.create(document.getElementById('editor'), {
            model: model,
            theme: message.dark ? 'vs-dark' : 'vs',
            automaticLayout: true,
            minimap: { enabled: false },
            fontFamily: "'Cascadia Mono', Consolas, 'Courier New', monospace",
            fontSize: 14,
            scrollBeyondLastLine: false,
            fixedOverflowWidgets: true,
            quickSuggestions: { strings: true, other: true, comments: false },
            wordBasedSuggestions: 'off',
            formatOnPaste: false,
        });

        model.onDidChangeContent(scheduleChanged);
        monaco.editor.onDidChangeMarkers(uris => {
            if (uris.some(uri => uri.toString() === model.uri.toString()))
                scheduleChanged();
        });
        editor.onDidChangeCursorPosition(e => post({ type: 'cursor', line: e.position.lineNumber, column: e.position.column }));
        editor.focus();
        sendChanged();
    }

    host.addEventListener('message', event => {
        const message = event.data;
        if (message.type === 'init') {
            init(message);
            return;
        }
        if (!editor)
            return;
        switch (message.type) {
            case 'setText':
                generation = message.generation;
                lastSent = '';
                model.setValue(message.text);
                editor.setPosition({ lineNumber: 1, column: 1 });
                editor.focus();
                break;
            case 'format':
                editor.getAction('editor.action.formatDocument').run().then(() => editor.focus());
                break;
            case 'reveal':
                editor.revealLineInCenter(message.line);
                editor.setPosition({ lineNumber: message.line, column: message.column });
                editor.focus();
                break;
            case 'appMarkers':
                monaco.editor.setModelMarkers(model, APP_OWNER, message.markers.map(m => {
                    const line = Math.min(Math.max(m.line, 1), model.getLineCount());
                    return {
                        startLineNumber: line,
                        startColumn: Math.max(m.column, 1),
                        endLineNumber: line,
                        endColumn: model.getLineMaxColumn(line),
                        message: m.message,
                        severity: monaco.MarkerSeverity.Error,
                    };
                }));
                break;
            case 'theme':
                monaco.editor.setTheme(message.dark ? 'vs-dark' : 'vs');
                break;
            case 'focus':
                editor.focus();
                break;
        }
    });

    require.config({ paths: { vs: '../Monaco/vs' } });
    require(['vs/editor/editor.main'], () => post({ type: 'ready' }));
})();
