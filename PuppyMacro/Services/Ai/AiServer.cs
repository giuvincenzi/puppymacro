using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PuppyMacro.Resources;

namespace PuppyMacro.Services.Ai;

/// <summary>An AI app connected right now: which app it is, for the AI page's cards, and its name as shown ("Claude (claude-ai)").</summary>
internal sealed record AiClient(int Session, AiAppKind Kind, string Name, DateTime Since);

/// <summary>The AI apps the AI page has a card for, recognized from the name they give; any other is Other.</summary>
internal enum AiAppKind
{
    Other,
    ClaudeDesktop,
    VsCode,
    Codex,
}

/// <summary>
/// PuppyMacro's MCP server: waits on <see cref="AiPipe"/> for bridges (<see cref="AiBridge"/>) and runs one MCP session
/// per bridge with <see cref="AiTools"/>. Started by <c>MainWindow</c>, always (with Allow AI access off the tools refuse,
/// so the AI app can tell the user why).
/// </summary>
internal sealed class AiServer : IDisposable
{
    private readonly AiTools _tools;
    private readonly AiActivityLog _log;
    private readonly Func<bool> _allowed;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly List<AiClient> _clients = new();
    private int _nextSession;

    public AiServer(AiTools tools, AiActivityLog log, Func<bool> allowed)
    {
        _tools = tools;
        _log = log;
        _allowed = allowed;
    }

    /// <summary>Raised on a server thread when an AI app connects or disconnects.</summary>
    public event Action? ClientsChanged;

    public List<AiClient> Clients
    {
        get
        {
            lock (_sync)
                return _clients.ToList();
        }
    }

    public void Start() => _ = Task.Run(AcceptLoop);

    /// <summary>Why AI apps can't connect right now (the pipe could not be made), or null. The AI page shows it.</summary>
    public string? ListenError { get; private set; }

    private void SetListenError(string? error)
    {
        if (error == ListenError)
            return;
        ListenError = error;
        ClientsChanged?.Invoke();
    }

    /// <summary>
    /// Waits for bridges, one pipe instance each, until <see cref="Dispose"/>. While another program holds the pipe's name
    /// (or creating it fails), tries again every few seconds: AI apps can't connect meanwhile.
    /// </summary>
    private async Task AcceptLoop()
    {
        bool first = true;
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = AiPipe.CreateServer(first);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                SetListenError(ex is IOException && first
                    ? Strings.AiApps_PipeInUse
                    : string.Format(CultureInfo.CurrentCulture, Strings.AiApps_CantConnect, ex.Message));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            first = false;
            SetListenError(null);
            NamedPipeServerStream connected = pipe;
            _ = Task.Run(() => Serve(connected));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        int session = Interlocked.Increment(ref _nextSession);
        // One debug session and one rate limit for this connection.
        DebugSession? debug = null;
        var limit = new AiRateLimit();
        // The first tool answer of the connection also gives the global rules and what the Library holds (AiTools.FirstAnswer).
        int answered = 0;
        McpServerOptions options = Options(_tools.All, AiTools.InstructionsText, async request =>
        {
            // resources/read: the same reading as read_resource (AiTools.OpenResource), logged and limited as a tool call.
            debug ??= StartDebug(request.Server);
            string uri = request.Params?.Uri ?? "";
            AiTools.OpenedResource? opened = null;
            AiToolResult result = await Debugged(debug, limit, "resources_read", Task.FromResult<string?>(Path.GetFileName(uri)),
                JsonSerializer.SerializeToElement(new { uri }), request.Params?.Meta, () =>
                {
                    opened = _tools.OpenResource(uri);
                    return Task.FromResult(opened.File is { } file
                        ? new AiToolResult($"{file.MimeType}, {DebugStore.SizeText(new FileInfo(file.Path).Length)}")
                        : AiToolResult.Error(opened.Problem ?? ""));
                }, CancellationToken.None).ConfigureAwait(false);
            return opened ?? new AiTools.OpenedResource(null, result.Text, false);
        }, (tool, args, request, cancel) =>
        {
            debug ??= StartDebug(request.Server);
            return Debugged(debug, limit, tool.Name, _tools.SubjectOf(args), args, request.Params?.Meta, async () =>
            {
                AiToolResult result = await _tools.Call(tool.Name, args, FriendlyName(request.Server.ClientInfo), cancel).ConfigureAwait(false);
                // Given once, with the first answer while AI access is on.
                if (Volatile.Read(ref answered) == 0 && await _tools.FirstAnswerIfAllowed().ConfigureAwait(false) is string first
                    && Interlocked.Exchange(ref answered, 1) == 0)
                    return result with { Notes = result.Notes.Append(first).ToList() };
                return result;
            }, cancel);
        });

        try
        {
            await using (pipe)
            await using (var transport = new StreamServerTransport(pipe, pipe, "PuppyMacro"))
            await using (McpServer server = McpServer.Create(transport, options))
            {
                Task run = server.RunAsync(_stop.Token);
                _ = Task.Run(() => WatchClient(server, session, run));
                await run.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            bool removed;
            lock (_sync)
            {
                removed = _clients.RemoveAll(c => c.Session == session) > 0;
                if (!removed)
                    _ended.Add(session); // not listed yet: WatchClient must not add it after this
            }
            if (removed)
                ClientsChanged?.Invoke();
        }
    }

    /// <summary>Sessions that ended (under <see cref="_sync"/>).</summary>
    private readonly HashSet<int> _ended = new();

    /// <summary>Adds the AI app to <see cref="Clients"/> once it has said its name (the MCP handshake).</summary>
    private async Task WatchClient(McpServer server, int session, Task run)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (server.ClientInfo == null && !run.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(100).ConfigureAwait(false);
        if (server.ClientInfo == null || run.IsCompleted)
            return;

        string name = FriendlyName(server.ClientInfo);
        lock (_sync)
        {
            if (_ended.Remove(session))
                return; // it disconnected meanwhile
            _clients.Add(new AiClient(session, KindOf(server.ClientInfo), name, DateTime.Now));
        }
        if (_allowed())
            _log.Add(name, AiActivityKind.Connection, Phrase.Of("Ai_Connected"));
        else
            _log.Add(name, AiActivityKind.Connection, Phrase.Of("Ai_ConnectedWhileOff"), AiActivityResult.Refused);
        ClientsChanged?.Invoke();
    }

    /// <summary>The name of an AI app that gave none.</summary>
    public const string UnknownApp = "Unknown app";

    /// <summary>
    /// Which app it is, from the name it gives (for the AI page's cards): Claude Desktop names itself "claude-ai" (its
    /// chat) or "local-agent-mode-" and the server's name in its settings (its agent mode); Claude Code ("claude-code") has
    /// no card.
    /// </summary>
    internal static AiAppKind KindOf(Implementation? client)
    {
        string lower = (client?.Title ?? client?.Name ?? "").Trim().ToLowerInvariant();
        if (lower.Contains("claude-code") || lower.Contains("claude code"))
            return AiAppKind.Other;
        if (lower.Contains("claude") || lower.StartsWith("local-agent-mode", StringComparison.Ordinal))
            return AiAppKind.ClaudeDesktop;
        if (lower.Contains("visual studio code") || lower.Contains("vscode"))
            return AiAppKind.VsCode;
        if (lower.Contains("codex"))
            return AiAppKind.Codex;
        return AiAppKind.Other;
    }

    /// <summary>
    /// The AI app as people read it everywhere (the activity, the status bar, the Done window, the debug folders). A Claude
    /// app is "Claude" with the name it gave, so its chat, its agent mode and Claude Code read apart ("Claude (claude-ai)",
    /// "Claude (local-agent-mode-puppymacro)", "Claude (claude-code)"); then "Visual Studio Code", "Codex", or the name
    /// any other app gave.
    /// </summary>
    internal static string FriendlyName(Implementation? client)
    {
        string name = (client?.Title ?? client?.Name ?? "").Trim();
        string lower = name.ToLowerInvariant();
        if (lower.Contains("claude") || lower.StartsWith("local-agent-mode", StringComparison.Ordinal))
            return $"Claude ({name})";
        return KindOf(client) switch
        {
            AiAppKind.VsCode => "Visual Studio Code",
            AiAppKind.Codex => "Codex",
            _ => name.Length == 0 ? UnknownApp : name,
        };
    }

    /// <summary>The connection's debug session, with who connected and how (its session.json).</summary>
    private static DebugSession StartDebug(McpServer server) =>
        DebugSession.Start(FriendlyName(server.ClientInfo), new { server.ClientInfo, server.ClientCapabilities });

    /// <summary>
    /// Every request of an AI app (a tool call, a resources/read) runs here: within this connection's rate limit
    /// (<see cref="AiRateLimit"/>), with its debug folder (request.json before, response.json after:
    /// <see cref="DebugCall"/>), whatever happens. <paramref name="what"/> names the folder: the tool, or "resources_read".
    /// </summary>
    private static async Task<AiToolResult> Debugged(DebugSession debug, AiRateLimit limit, string what,
        Task<string?> subject, JsonElement args, object? meta, Func<Task<AiToolResult>> run, CancellationToken cancel)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        JsonElement? metaJson = meta != null ? JsonSerializer.SerializeToElement(meta) : null;
        DebugCall call = debug.NewCall(what, await subject.ConfigureAwait(false), args, metaJson);
        AiToolResult result = AiToolResult.Error("PuppyMacro could not finish the request.");
        try
        {
            cancel.ThrowIfCancellationRequested();
            if (limit.Wait() is TimeSpan wait)
                return result = AiToolResult.Error(
                    $"Too many requests at once: PuppyMacro takes about {AiRateLimit.PerSecond} a second. Try again in {Math.Ceiling(wait.TotalSeconds)} s.");
            DebugCall.Current = call;
            return result = WithFileList(await run().ConfigureAwait(false));
        }
        finally
        {
            DebugCall.Current = null;
            call.Answered(result, watch.Elapsed.TotalMilliseconds, result.Files.Select(f => new
            {
                Uri = DebugStore.UriOf(f),
                Name = Path.GetFileName(f),
                Description = DebugStore.Describe(f),
            }).Concat<object>(result.Images.Select(i => new { Image = i.MimeType, Bytes = i.Data.Length })));
        }
    }

    /// <summary>
    /// The answer's text with, at its end, a line for each linked file: its name, what it holds, its size in pixels or
    /// lines, and its weight. The same is in each resource link, but some AI apps show the model only a link's name and
    /// URI (Claude Desktop did), so the text says it too, built here for every tool.
    /// </summary>
    internal static AiToolResult WithFileList(AiToolResult result)
    {
        if (result.Files.Count == 0)
            return result;
        var text = new StringBuilder(result.Text).Append("\n\nLinked files:");
        foreach (string file in result.Files)
        {
            var info = new FileInfo(file);
            text.Append("\n- ").Append(info.Name).Append(": ").Append(DebugStore.Describe(file));
            if (info.Exists)
                text.Append(", ").Append(DebugStore.SizeText(info.Length));
        }
        return result with { Text = text.ToString() };
    }

    /// <summary>The MCP server's options: PuppyMacro, its instructions, the tools (each answered by <paramref name="run"/>) and the debug data as resources.</summary>
    internal static McpServerOptions Options(IEnumerable<AiTool> tools, string instructions,
        Func<RequestContext<ReadResourceRequestParams>, Task<AiTools.OpenedResource>> read,
        Func<AiTool, JsonElement, RequestContext<CallToolRequestParams>, CancellationToken, Task<AiToolResult>> run)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "PuppyMacro", Version = Version() },
            ServerInstructions = instructions,
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
            ResourceCollection = new McpServerResourceCollection(),
        };
        foreach (AiTool tool in tools)
            options.ToolCollection.Add(new McpTool(tool, run));
        options.ResourceCollection.Add(new DebugResource(read));
        return options;
    }

    private static string Version()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>
    /// An <see cref="AiTool"/> as the SDK's tool: its schema as given, its answer as text, and the files it points to as
    /// resource links (their name, type, size and what they hold, <see cref="DebugStore.Describe"/>: built here for every
    /// tool; the AI app reads them with resources/read or read_resource if it needs them), and the pictures an answer gives.
    /// </summary>
    private sealed class McpTool : McpServerTool
    {
        private readonly AiTool _tool;
        private readonly Func<AiTool, JsonElement, RequestContext<CallToolRequestParams>, CancellationToken, Task<AiToolResult>> _run;

        public McpTool(AiTool tool, Func<AiTool, JsonElement, RequestContext<CallToolRequestParams>, CancellationToken, Task<AiToolResult>> run)
        {
            _tool = tool;
            _run = run;
            using JsonDocument schema = JsonDocument.Parse(tool.InputSchema);
            ProtocolTool = new Tool
            {
                Name = tool.Name,
                Title = tool.Title,
                Description = tool.Description,
                InputSchema = schema.RootElement.Clone(),
                Annotations = new ToolAnnotations
                {
                    Title = tool.Title,
                    ReadOnlyHint = tool.ReadOnly,
                    DestructiveHint = tool.Destructive,
                    IdempotentHint = tool.Idempotent,
                    OpenWorldHint = false,
                },
            };
        }

        public override Tool ProtocolTool { get; }

        public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            JsonElement args = JsonSerializer.SerializeToElement(
                request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
            AiToolResult result = await _run(_tool, args, request, cancellationToken).ConfigureAwait(false);
            var content = new List<ContentBlock> { new TextContentBlock { Text = result.Text } };
            foreach (string note in result.Notes)
                content.Add(new TextContentBlock { Text = note });
            foreach (string file in result.Files)
            {
                var info = new FileInfo(file);
                content.Add(new ResourceLinkBlock
                {
                    Uri = DebugStore.UriOf(file),
                    Name = info.Name,
                    Description = DebugStore.Describe(file),
                    MimeType = DebugStore.MimeOf(file),
                    Size = info.Exists ? info.Length : null,
                });
            }
            foreach (AiImage image in result.Images)
                content.Add(ImageContentBlock.FromBytes(image.Data, image.MimeType));
            return new CallToolResult
            {
                Content = content,
                IsError = result.IsError,
            };
        }
    }

    /// <summary>
    /// The debug data as MCP resources (puppymacro://debug/{path}): resources/read gives a file of the debug folder, a log
    /// or JSON as text, a picture as its PNG (base64, as MCP carries binary data), through <see cref="AiTools.OpenResource"/>
    /// as the read_resource tool. Only files inside the folder: any other path, "..", or a file that is not there answers
    /// "Resource not found" (-32002); with AI access or Let AI apps see the screen off, "Invalid request" (-32600) and why.
    /// </summary>
    private sealed class DebugResource(Func<RequestContext<ReadResourceRequestParams>, Task<AiTools.OpenedResource>> read) : McpServerResource
    {
        public override ResourceTemplate ProtocolResourceTemplate { get; } = new()
        {
            UriTemplate = DebugStore.UriPrefix + "{+path}",
            Name = "debug",
            Title = "PuppyMacro debug data",
            Description = "Files PuppyMacro keeps about AI requests and macro runs: logs, the screen's pictures, requests and answers.",
        };

        public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

        public override bool IsMatch(string uri) => uri.StartsWith(DebugStore.UriPrefix, StringComparison.Ordinal);

        public override async ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            string uri = request.Params?.Uri ?? "";
            AiTools.OpenedResource opened = await read(request).ConfigureAwait(false);
            if (opened.File is not { } file)
                throw new ModelContextProtocol.McpProtocolException(opened.Problem ?? $"Resource not found: {uri}",
                    opened.Missing ? ModelContextProtocol.McpErrorCode.ResourceNotFound : ModelContextProtocol.McpErrorCode.InvalidRequest);
            ResourceContents contents = file.IsImage
                ? BlobResourceContents.FromBytes(File.ReadAllBytes(file.Path), uri, file.MimeType)
                : new TextResourceContents { Uri = uri, MimeType = file.MimeType, Text = opened.Text ?? "" };
            return new ReadResourceResult { Contents = new List<ResourceContents> { contents } };
        }
    }
}
