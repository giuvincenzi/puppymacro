using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// PuppyMacro's MCP server as an AI app sees it: one connection, served by <c>AiServer.Serve</c> over a pipe of the test's
/// own (never AiPipe's name, which a running PuppyMacro owns), spoken to in MCP's JSON-RPC, one message per line.
/// </summary>
// DebugStore.Root is one folder for the whole process: these classes set it, one at a time.
[Collection(nameof(DebugStore))]
public class AiServerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiActivityLog _log;
    private readonly AiServer _server;

    public AiServerTests()
    {
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _settings.Loops.Clear();
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _host.Macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Secret raid", Domain = LibraryDomain.Game, AppExe = "Diablo IV.exe",
            Components = { new LibraryComponent { Name = "Press E", Purpose = ComponentPurpose.Act, Concept = "press_e",
                Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x45 } } } },
        });
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _server = new AiServer(new AiTools(_host, _log), _log, () => _settings.AllowAiAccess);
    }

    public void Dispose()
    {
        _server.Dispose();
        _folder.Dispose();
    }

    /// <summary>An AI app connected to the server, after MCP's handshake.</summary>
    private sealed class Client : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private int _id;

        private Client(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false));
            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public static async Task<Client> Connect(AiServer server)
        {
            string name = "PuppyMacro.Tests." + Guid.NewGuid().ToString("N");
            var serverEnd = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var clientEnd = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task waiting = serverEnd.WaitForConnectionAsync();
            await clientEnd.ConnectAsync(5000);
            await waiting;
            MethodInfo serve = typeof(AiServer).GetMethod("Serve", BindingFlags.NonPublic | BindingFlags.Instance)!;
            _ = (Task)serve.Invoke(server, new object[] { serverEnd })!;

            var client = new Client(clientEnd);
            await client.Request("initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "test-app", version = "1.0" },
            });
            await client.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            return client;
        }

        private Task Send(object message) => _writer.WriteLineAsync(JsonSerializer.Serialize(message));

        /// <summary>A request and its answer (its "result" or its "error").</summary>
        public async Task<JsonNode> Request(string method, object parameters)
        {
            int id = ++_id;
            await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                string line = await _reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("The server closed the connection.");
                JsonNode message = JsonNode.Parse(line)!;
                if (message["id"]?.GetValueKind() == JsonValueKind.Number && message["id"]!.GetValue<int>() == id)
                    return message["result"] ?? message["error"]!;
            }
        }

        public Task<JsonNode> CallTool(string tool, object? arguments = null) =>
            Request("tools/call", new { name = AiTools.Name(tool), arguments = arguments ?? new { } });

        public async ValueTask DisposeAsync() => await _pipe.DisposeAsync();
    }

    private static string[] Texts(JsonNode result) =>
        result["content"]!.AsArray().Where(b => b!["type"]!.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task The_first_answer_gives_the_rules_and_the_Library_once_per_connection()
    {
        await using Client client = await Client.Connect(_server);

        JsonNode first = await client.CallTool("get_settings");
        JsonNode second = await client.CallTool("get_settings");

        Assert.Equal(2, Texts(first).Length);
        Assert.Contains(AiTools.GlobalRules[0], Texts(first)[1]);
        Assert.Contains("\"Secret raid\"", Texts(first)[1]);
        Assert.Single(Texts(second));
        JsonDocument.Parse(Texts(first)[0]).Dispose(); // the answer's own JSON stays whole
    }

    [Fact]
    public async Task While_AI_access_is_off_the_first_answer_gives_nothing_of_the_Library_and_keeps_the_rules_for_later()
    {
        _settings.AllowAiAccess = false;
        await using Client client = await Client.Connect(_server);

        JsonNode refused = await client.CallTool("get_overview");
        _settings.AllowAiAccess = true;
        JsonNode allowed = await client.CallTool("get_overview");

        Assert.True(refused["isError"]!.GetValue<bool>());
        // AiServer.Serve adds AiTools.FirstAnswer to the connection's first answer, whatever it is: also to the refusal,
        // so the Library's collections reach an AI app while Allow AI access is off, and once the user turns it on the
        // rules never come.
        Assert.DoesNotContain(Texts(refused), t => t.Contains("Secret raid", StringComparison.Ordinal));
        Assert.Contains(Texts(allowed), t => t.Contains(AiTools.GlobalRules[0], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("puppymacro://debug/../secret.txt")]
    [InlineData("puppymacro://debug/%2e%2e/secret.txt")]
    [InlineData("puppymacro://debug/..%2fsecret.txt")]
    [InlineData("puppymacro://debug/..%5csecret.txt")]
    [InlineData("puppymacro://debug/C:%5cWindows%5cwin.ini")]
    [InlineData("puppymacro://debug/%5c%5clocalhost%5cc$%5cWindows%5cwin.ini")]
    [InlineData("file:///C:/Windows/win.ini")]
    public async Task Resources_read_gives_nothing_outside_the_debug_data(string uri)
    {
        File.WriteAllText(_folder.File("secret.txt"), "Kept away 4711");
        Directory.CreateDirectory(DebugStore.Root);
        await using Client client = await Client.Connect(_server);

        JsonNode answer = await client.Request("resources/read", new { uri });
        JsonNode tool = await client.CallTool("read_resource", new { uri });

        Assert.NotNull(answer["code"]); // an error, not contents
        Assert.DoesNotContain("Kept away 4711", answer.ToJsonString());
        Assert.True(tool["isError"]!.GetValue<bool>());
        Assert.DoesNotContain("Kept away 4711", tool.ToJsonString());
    }

    [Fact]
    public async Task Tools_list_names_every_tool_with_its_annotations()
    {
        await using Client client = await Client.Connect(_server);

        JsonNode list = await client.Request("tools/list", new { });

        var tools = list["tools"]!.AsArray();
        Assert.Equal(AiTools.Definitions().Count, tools.Count);
        Assert.All(tools, t =>
        {
            Assert.StartsWith(AiTools.Prefix, t!["name"]!.GetValue<string>());
            Assert.NotNull(t["annotations"]?["readOnlyHint"]);
            Assert.False(t["annotations"]!["openWorldHint"]!.GetValue<bool>());
        });
    }
}
