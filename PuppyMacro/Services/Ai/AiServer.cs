using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PuppyMacro.Services.Ai;

/// <summary>An AI app connected right now, by the name it gave ("Claude Desktop").</summary>
internal sealed record AiClient(int Session, string Name, DateTime Since);

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
                    ? "Another program uses PuppyMacro's AI connection, so AI apps can't connect. Close it, or restart Windows."
                    : $"AI apps can't connect: {ex.Message}");
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
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "PuppyMacro", Version = Version() },
            ServerInstructions = AiTools.Instructions,
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
        };
        foreach (AiTool tool in _tools.All)
            options.ToolCollection.Add(new McpTool(tool, _tools));

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
            _clients.Add(new AiClient(session, name, DateTime.Now));
        }
        if (_allowed())
            _log.Add(name, AiActivityKind.Connection, "Connected");
        else
            _log.Add(name, AiActivityKind.Connection, "Tried to connect while AI access was off", AiActivityResult.Refused);
        ClientsChanged?.Invoke();
    }

    /// <summary>"Claude Desktop", "Visual Studio Code", "Codex", or the name the app gave.</summary>
    internal static string FriendlyName(Implementation? client)
    {
        string name = (client?.Title ?? client?.Name ?? "").Trim();
        string lower = name.ToLowerInvariant();
        if (lower.Contains("claude-code") || lower.Contains("claude code"))
            return "Claude Code";
        if (lower.Contains("claude"))
            return "Claude Desktop";
        if (lower.Contains("visual studio code") || lower.Contains("vscode"))
            return "Visual Studio Code";
        if (lower.Contains("codex"))
            return "Codex";
        return name.Length == 0 ? "Unknown app" : name;
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

    /// <summary>An <see cref="AiTool"/> as the SDK's tool: its schema as given, its answer as text.</summary>
    private sealed class McpTool : McpServerTool
    {
        private readonly AiTool _tool;
        private readonly AiTools _tools;

        public McpTool(AiTool tool, AiTools tools)
        {
            _tool = tool;
            _tools = tools;
            using JsonDocument schema = JsonDocument.Parse(tool.InputSchema);
            ProtocolTool = new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = schema.RootElement.Clone(),
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = tool.ReadOnly,
                    DestructiveHint = tool.Destructive,
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
            string app = FriendlyName(request.Server.ClientInfo);
            AiToolResult result = await _tools.Call(_tool.Name, args, app, cancellationToken).ConfigureAwait(false);
            return new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = result.Text } },
                IsError = result.IsError,
            };
        }
    }
}
