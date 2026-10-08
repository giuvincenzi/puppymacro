// The macro editor's Code view, inside a WebView2 (Views/MacroCodeView.xaml.cs): Monaco and a
// Problems panel like VS Code's (Monaco has no panel of its own: it is only VS Code's editor).
// Messages from the app: init { text, generation, schema, dark }, setText { text, generation }, format,
// appMarkers { version, markers: [{ line, column, message }] }, theme { dark }, focus.
// Messages to the app: ready, changed { text, generation, version, markers: [{ line, column, message }] }.
// generation: which text the app last set, so it can ignore reports about older text.
// version: Monaco's version of the text the app checked, so "No problems." is only shown for checked text.
(function () {
    'use strict';

    const host = window.chrome.webview;
    const APP_OWNER = 'puppymacro'; // markers set by the app (MacroJson checks)
    let editor = null;
    let model = null;
    let changeTimer = 0;
    let lastSent = '';
    let generation = 0;
    let checkedVersion = -1;
    let selected = 0;

    const list = document.getElementById('list');
    const empty = document.getElementById('empty');
    const count = document.getElementById('count');

    function post(message) {
        host.postMessage(message);
    }

    function setTheme(dark) {
        document.body.className = dark ? 'vs-dark' : 'vs';
        monaco.editor.setTheme(dark ? 'vs-dark' : 'vs');
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

    // ---- Problems panel ----

    function problems() {
        return monaco.editor.getModelMarkers({ resource: model.uri })
            .filter(m => m.severity >= monaco.MarkerSeverity.Warning)
            .sort((a, b) => a.startLineNumber - b.startLineNumber || a.startColumn - b.startColumn);
    }

    function renderProblems() {
        const items = problems();
        list.replaceChildren();
        items.forEach((marker, index) => {
            const row = document.createElement('li');
            row.setAttribute('role', 'option');
            row.className = index === selected ? 'selected' : '';
            const icon = document.createElement('span');
            icon.className = 'codicon codicon-error';
            icon.setAttribute('aria-hidden', 'true');
            const message = document.createElement('span');
            message.className = 'message';
            message.textContent = marker.message;
            message.title = marker.message;
            const position = document.createElement('span');
            position.className = 'position';
            position.textContent = `Ln ${marker.startLineNumber}, Col ${marker.startColumn}`;
            row.append(icon, message, position);
            row.setAttribute('aria-label', `${marker.message} Ln ${marker.startLineNumber}, Col ${marker.startColumn}`);
            row.addEventListener('click', () => { select(index); reveal(marker); });
            list.append(row);
        });
        if (selected >= items.length)
            selected = 0;
        count.hidden = items.length === 0;
        count.textContent = String(items.length);
        empty.hidden = items.length > 0;
        empty.textContent = checkedVersion === model.getVersionId() ? 'No problems.' : 'Checking…';
    }

    function select(index) {
        const rows = list.children;
        if (rows.length === 0)
            return;
        selected = Math.max(0, Math.min(index, rows.length - 1));
        for (let i = 0; i < rows.length; i++)
            rows[i].className = i === selected ? 'selected' : '';
        rows[selected].scrollIntoView({ block: 'nearest' });
    }

    function reveal(marker) {
        editor.revealLineInCenter(marker.startLineNumber);
        editor.setPosition({ lineNumber: marker.startLineNumber, column: marker.startColumn });
        editor.focus();
    }

    list.addEventListener('keydown', e => {
        if (e.key === 'ArrowDown') select(selected + 1);
        else if (e.key === 'ArrowUp') select(selected - 1);
        else if (e.key === 'Enter') { const marker = problems()[selected]; if (marker) reveal(marker); }
        else return;
        e.preventDefault();
    });

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
        setTheme(message.dark);

        model.onDidChangeContent(() => { renderProblems(); scheduleChanged(); });
        monaco.editor.onDidChangeMarkers(uris => {
            if (!uris.some(uri => uri.toString() === model.uri.toString()))
                return;
            renderProblems();
            scheduleChanged();
        });
        editor.focus();
        renderProblems();
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
                checkedVersion = -1;
                model.setValue(message.text);
                editor.setPosition({ lineNumber: 1, column: 1 });
                editor.focus();
                break;
            case 'format':
                editor.getAction('editor.action.formatDocument').run().then(() => editor.focus());
                break;
            case 'appMarkers':
                checkedVersion = message.version;
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
                renderProblems();
                break;
            case 'theme':
                setTheme(message.dark);
                break;
            case 'focus':
                editor.focus();
                break;
        }
    });

    require.config({ paths: { vs: '../Monaco/vs' } });
    require(['vs/editor/editor.main'], () => post({ type: 'ready' }));
})();
