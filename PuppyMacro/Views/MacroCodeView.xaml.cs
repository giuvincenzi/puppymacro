using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PuppyMacro.Services;
using Wpf.Ui.Appearance;

namespace PuppyMacro.Views;

/// <summary>
/// The macro editor's Code view: the macro file's JSON in Monaco (Assets\CodeEditor, Assets\Monaco)
/// inside a WebView2, with a Problems panel like VS Code's in the same page. Problems come from
/// Monaco (JSON syntax and <see cref="MacroSchema"/>) and, when Monaco finds none, from
/// <see cref="Check"/> (MacroJson.Parse), which the page underlines and lists too.
/// </summary>
public partial class MacroCodeView
{
    private const string Host = "puppymacro.editor";
    private const string PageUrl = "https://" + Host + "/CodeEditor/editor.html";

    private WebView2? _webView;
    private bool _editorReady;
    private string _text = "";
    private int _generation;
    private List<CodeProblem> _editorProblems = new();
    private List<CodeProblem> _appProblems = new();

    public MacroCodeView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Close();
    }

    /// <summary>Checks the text (MacroJson.Parse) and returns its problems.</summary>
    internal Func<string, List<CodeProblem>>? Check { get; set; }

    /// <summary>The JSON Schema for the edited macro (<see cref="MacroSchema.Build"/>).</summary>
    internal string Schema { get; set; } = "{}";

    /// <summary>Raised when the text or the problems change.</summary>
    public event Action? Changed;

    /// <summary>The text in the editor, as last reported by it.</summary>
    public string Text => _text;

    /// <summary>The editor reported on the text last shown: <see cref="Problems"/> is about it.</summary>
    public bool IsUpToDate { get; private set; }

    internal IReadOnlyList<CodeProblem> Problems => _editorProblems.Count > 0 ? _editorProblems : _appProblems;

    /// <summary>Shows <paramref name="text"/> in the editor. Returns why it cannot, or null.</summary>
    internal async Task<string?> ShowAsync(string text)
    {
        _text = text;
        _generation++;
        IsUpToDate = false;
        _editorProblems = new();
        _appProblems = new();

        if (_webView == null)
            return await CreateAsync();
        if (_editorReady)
            Post(new JsonObject { ["type"] = "setText", ["text"] = text, ["generation"] = _generation });
        return null;
    }

    public void Format() => Post(new JsonObject { ["type"] = "format" });

    public void FocusEditor()
    {
        _webView?.Focus();
        Post(new JsonObject { ["type"] = "focus" });
    }

    private async Task<string?> CreateAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewFolder);
            _webView = new WebView2
            {
                // The editor's own background, so nothing flashes while it loads.
                DefaultBackgroundColor = IsDark ? System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E) : System.Drawing.Color.White,
            };
            System.Windows.Automation.AutomationProperties.SetName(_webView, "Code editor");
            EditorHost.Children.Add(_webView);
            await _webView.EnsureCoreWebView2Async(environment);

            CoreWebView2 core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = App.IsDevBuild;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, AppPaths.WebAssetsFolder, CoreWebView2HostResourceAccessKind.Deny);
            // Only the editor page: no links, no new windows, nothing from the network.
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(PageUrl, StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += (_, _) => ShowStatus("The code editor stopped. Close the macro editor and open it again.");
            core.Navigate(PageUrl);
            return null;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or ArgumentException)
        {
            Close();
            return ex is WebView2RuntimeNotFoundException
                ? "Code view needs the Microsoft Edge WebView2 Runtime. It comes with Windows 11 and with the PuppyMacro installer; install it from Microsoft and open the editor again."
                : $"The code editor could not start: {ex.Message}";
        }
    }

    private static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    private void Post(JsonObject message)
    {
        if (_editorReady)
            _webView?.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(e.WebMessageAsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        switch ((string?)message?["type"])
        {
            case "ready":
                _editorReady = true;
                StatusText.Visibility = Visibility.Collapsed;
                Post(new JsonObject
                {
                    ["type"] = "init",
                    ["text"] = _text,
                    ["generation"] = _generation,
                    ["schema"] = JsonNode.Parse(Schema),
                    ["dark"] = IsDark,
                });
                break;

            case "changed":
                // Reports about text replaced since (ShowAsync) are out of date.
                if ((int?)message["generation"] != _generation)
                    return;
                _text = (string?)message["text"] ?? "";
                _editorProblems = (message["markers"] as JsonArray ?? new JsonArray())
                    .Select(m => new CodeProblem((int?)m?["line"] ?? 1, (int?)m?["column"] ?? 1, (string?)m?["message"] ?? ""))
                    .ToList();
                // Syntax and schema first (Monaco explains them best); then the app's own checks.
                _appProblems = _editorProblems.Count == 0 ? Check?.Invoke(_text) ?? new() : new();
                Post(new JsonObject
                {
                    ["type"] = "appMarkers",
                    ["version"] = (int?)message["version"],
                    ["markers"] = new JsonArray(_appProblems
                        .Select(p => (JsonNode?)new JsonObject { ["line"] = p.Line, ["column"] = p.Column, ["message"] = p.Message })
                        .ToArray()),
                });
                IsUpToDate = true;
                Changed?.Invoke();
                break;
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        if (_webView != null)
            _webView.Visibility = Visibility.Collapsed;
    }

    private void Close()
    {
        _webView?.Dispose();
        _webView = null;
        _editorReady = false;
        EditorHost.Children.Clear();
    }
}
