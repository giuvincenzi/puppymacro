using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

public class AiAppsTests : IDisposable
{
    private const string Exe = @"C:\Users\Someone\AppData\Local\PuppyMacro\current\PuppyMacro.exe";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Claude_Desktop_gets_PuppyMacro_next_to_its_other_servers()
    {
        string config = _folder.File("claude_desktop_config.json");
        File.WriteAllText(config, """{ "mcpServers": { "other": { "command": "other.exe" } }, "theme": "dark" }""");

        Assert.False(AiApps.ClaudeHas(config, Exe));
        Assert.Null(AiApps.ClaudeAdd(config, Exe));

        Assert.True(AiApps.ClaudeHas(config, Exe));
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(config)))
        {
            JsonElement servers = doc.RootElement.GetProperty("mcpServers");
            Assert.Equal("other.exe", servers.GetProperty("other").GetProperty("command").GetString());
            JsonElement entry = servers.GetProperty(AiApps.ServerName);
            Assert.Equal(Exe, entry.GetProperty("command").GetString());
            Assert.Equal("--mcp", entry.GetProperty("args")[0].GetString());
            Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        }

        Assert.Null(AiApps.ClaudeRemove(config));
        Assert.False(AiApps.ClaudeHas(config, Exe));
        Assert.Contains("other.exe", File.ReadAllText(config));
    }

    [Fact]
    public void A_missing_Claude_Desktop_file_is_created()
    {
        string config = Path.Combine(_folder.Path, "Claude", "claude_desktop_config.json");

        Assert.Null(AiApps.ClaudeAdd(config, Exe));

        Assert.True(AiApps.ClaudeHas(config, Exe));
    }

    [Fact]
    public void A_Claude_Desktop_file_with_an_error_is_never_replaced()
    {
        string config = _folder.File("claude_desktop_config.json");
        const string broken = """{ "mcpServers": { "other": """;
        File.WriteAllText(config, broken);

        string? error = AiApps.ClaudeAdd(config, Exe);

        Assert.NotNull(error);
        Assert.Contains("has an error", error);
        Assert.Equal(broken, File.ReadAllText(config));
    }

    [Theory]
    [InlineData("""{ "mcpServers": [] }""")]
    [InlineData("""{ "mcpServers": { "puppymacro": { "command": 42 } } }""")]
    [InlineData("""{ "mcpServers": { "puppymacro": "x" } }""")]
    [InlineData("""[ 1, 2 ]""")]
    [InlineData("""{ "a": 1, "a": 2 }""")]
    public void Unexpected_content_in_an_apps_file_is_read_as_not_connected_and_never_replaced(string json)
    {
        string config = _folder.File("claude_desktop_config.json");
        File.WriteAllText(config, json);

        Assert.False(AiApps.ClaudeHas(config, Exe));
        string? error = AiApps.ClaudeAdd(config, Exe);

        if (json.Contains("\"puppymacro\""))
        {
            // An object we can change: our entry is replaced, the rest kept.
            Assert.Null(error);
            Assert.True(AiApps.ClaudeHas(config, Exe));
        }
        else
        {
            Assert.NotNull(error);
            Assert.Equal(json, File.ReadAllText(config));
        }
    }

    [Fact]
    public void An_entry_for_another_PuppyMacro_does_not_count_as_connected()
    {
        string config = _folder.File("claude_desktop_config.json");
        AiApps.ClaudeAdd(config, @"C:\Other\PuppyMacro.exe");

        Assert.False(AiApps.ClaudeHas(config, Exe));
    }

    [Fact]
    public void Claude_Desktop_from_the_Microsoft_Store_reads_its_own_copy_when_there_is_one()
    {
        string appData = Path.Combine(_folder.Path, "Roaming");
        string localAppData = Path.Combine(_folder.Path, "Local");
        string virtualized = Path.Combine(localAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");

        Assert.Equal(Path.Combine(appData, "Claude", "claude_desktop_config.json"), AiApps.ClaudeConfigPath(appData, localAppData));
        Assert.False(AiApps.ClaudeInstalled(appData, localAppData));

        Directory.CreateDirectory(Path.GetDirectoryName(virtualized)!);
        Assert.True(AiApps.ClaudeInstalled(appData, localAppData));
        Assert.Equal(Path.Combine(appData, "Claude", "claude_desktop_config.json"), AiApps.ClaudeConfigPath(appData, localAppData));

        File.WriteAllText(virtualized, "{}");
        Assert.Equal(virtualized, AiApps.ClaudeConfigPath(appData, localAppData));
    }

    [Fact]
    public void Visual_Studio_Code_installs_through_its_link_and_reads_mcp_json_with_comments()
    {
        string uri = AiApps.VsCodeInstallUri(Exe);
        Assert.StartsWith("vscode:mcp/install?", uri);
        using (JsonDocument entry = JsonDocument.Parse(Uri.UnescapeDataString(uri["vscode:mcp/install?".Length..])))
        {
            Assert.Equal(AiApps.ServerName, entry.RootElement.GetProperty("name").GetString());
            Assert.Equal(Exe, entry.RootElement.GetProperty("command").GetString());
        }

        string config = _folder.File("mcp.json");
        string entryJson = new JsonObject { ["type"] = "stdio", ["command"] = Exe, ["args"] = new JsonArray("--mcp") }.ToJsonString();
        File.WriteAllText(config,
            "{\n  // added by VS Code\n  \"servers\": { \"" + AiApps.ServerName + "\": " + entryJson + ", },\n}\n");
        Assert.True(AiApps.VsCodeHas(config, Exe));

        // Rewriting it would lose the comment: the user removes it in VS Code.
        Assert.Contains("MCP: Open User Configuration", AiApps.VsCodeRemove(config));
        Assert.True(AiApps.VsCodeHas(config, Exe));
    }

    [Fact]
    public void Visual_Studio_Code_without_comments_is_removed_here()
    {
        string config = _folder.File("mcp.json");
        var root = new JsonObject
        {
            ["servers"] = new JsonObject
            {
                [AiApps.ServerName] = new JsonObject { ["command"] = Exe },
                ["other"] = new JsonObject { ["command"] = "x" },
            },
        };
        File.WriteAllText(config, root.ToJsonString());

        Assert.Null(AiApps.VsCodeRemove(config));

        Assert.False(AiApps.VsCodeHas(config, Exe));
        Assert.Contains("\"other\"", File.ReadAllText(config));
    }

    [Fact]
    public void Codex_gets_its_own_table_with_a_long_tool_timeout_and_keeps_the_rest()
    {
        string config = _folder.File("config.toml");
        File.WriteAllText(config, "model = \"gpt-5\"\n\n[mcp_servers.other]\ncommand = \"other\"\n");

        Assert.Null(AiApps.CodexAdd(config, Exe));
        Assert.Null(AiApps.CodexAdd(config, Exe)); // twice: still one table

        string text = File.ReadAllText(config);
        Assert.True(AiApps.CodexHas(config, Exe));
        Assert.Single(text.Split('\n'), l => l.Trim() == $"[mcp_servers.{AiApps.ServerName}]");
        Assert.Contains($"command = '{Exe}'", text);
        Assert.Contains("args = [\"--mcp\"]", text);
        Assert.Contains("tool_timeout_sec = 600", text);
        Assert.Contains("[mcp_servers.other]", text);
        Assert.StartsWith("model = \"gpt-5\"", text);

        Assert.Null(AiApps.CodexRemove(config));
        text = File.ReadAllText(config);
        Assert.False(AiApps.CodexHas(config, Exe));
        Assert.DoesNotContain("--mcp", text);
        Assert.Contains("[mcp_servers.other]\ncommand = \"other\"", text.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("[mcp_servers.puppymacro] # added by hand")]
    [InlineData("[ mcp_servers . puppymacro ]")]
    [InlineData("[mcp_servers.\"puppymacro\"]")]
    public void Codex_finds_its_table_however_its_header_is_written(string header)
    {
        string config = _folder.File("config.toml");
        File.WriteAllText(config, $"{header}\ncommand = '{Exe}'\nargs = [\"--mcp\"]\n\n# the other server\n[mcp_servers.other]\ncommand = \"other\"\n");

        Assert.True(AiApps.CodexHas(config, Exe));
        Assert.Null(AiApps.CodexAdd(config, Exe));

        string text = File.ReadAllText(config).Replace("\r\n", "\n");
        Assert.Single(text.Split('\n'), l => l.Contains("puppymacro"));
        Assert.Contains("# the other server\n[mcp_servers.other]", text);

        Assert.Null(AiApps.CodexRemove(config));
        Assert.Contains("# the other server\n[mcp_servers.other]", File.ReadAllText(config).Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_configuration_for_other_apps_has_the_usual_shape()
    {
        using JsonDocument doc = JsonDocument.Parse(AiApps.GenericConfiguration(Exe));

        JsonElement entry = doc.RootElement.GetProperty("mcpServers").GetProperty(AiApps.ServerName);
        Assert.Equal(Exe, entry.GetProperty("command").GetString());
        Assert.Equal("--mcp", entry.GetProperty("args")[0].GetString());
    }

    [Theory]
    [InlineData("claude-ai", null, "Claude Desktop")]
    [InlineData("local-agent-mode-puppymacro-dev", null, "Claude Desktop")]
    [InlineData("claude-code", null, "Claude Code")]
    [InlineData("Visual Studio Code", null, "Visual Studio Code")]
    [InlineData("codex-mcp-client", null, "Codex")]
    [InlineData("my-agent", "My Agent", "My Agent")]
    [InlineData("", null, "Unknown app")]
    public void AI_apps_are_named_as_people_know_them(string name, string? title, string expected) =>
        Assert.Equal(expected, AiServer.FriendlyName(new Implementation { Name = name, Title = title, Version = "1" }));
}

public class AiActivityLogTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Requests_survive_a_restart_and_old_ones_are_kept_whatever_their_age()
    {
        DateTime now = new(2026, 10, 11, 14, 0, 0);
        string path = _folder.File("ai-activity.json");
        var log = new AiActivityLog(path, () => now);

        now = now.AddDays(-400);
        log.Add("Claude Desktop", AiActivityKind.Read, "Old");
        now = now.AddDays(400);
        log.Add("Claude Desktop", AiActivityKind.Proposal, "New remap: Ctrl + X → A", AiActivityResult.Saved, data: "{}");

        var reloaded = new AiActivityLog(path, () => now);
        reloaded.Load();
        Assert.Equal(new[] { "New remap: Ctrl + X → A", "Old" }, reloaded.Entries().Select(e => e.Request));
        AiActivity entry = reloaded.Entries()[0];
        Assert.Equal(AiActivityResult.Saved, entry.Result);
        Assert.Equal("{}", entry.Data);
    }

    [Fact]
    public void Only_the_newest_requests_that_fit_in_the_size_limit_are_kept()
    {
        DateTime now = new(2026, 10, 11, 14, 0, 0);
        string path = _folder.File("ai-activity.json");
        var log = new AiActivityLog(path, () => now = now.AddMinutes(1), maxBytes: 20_000);
        string bigMacro = new('x', 3000);

        for (int i = 0; i < 50; i++)
            log.Add("Claude Desktop", AiActivityKind.Proposal, $"Change {i}", AiActivityResult.Saved, data: bigMacro, before: bigMacro);

        List<AiActivity> entries = log.Entries();
        Assert.InRange(entries.Count, 2, 49);
        Assert.Equal("Change 49", entries[0].Request);
        Assert.True(new FileInfo(path).Length <= 20_000, $"{new FileInfo(path).Length} bytes");
    }

    [Fact]
    public void Newest_first_and_clear()
    {
        DateTime now = new(2026, 10, 11, 14, 0, 0);
        var log = new AiActivityLog(_folder.File("ai-activity.json"), () => now);
        int changes = 0;
        log.Changed += () => changes++;

        log.Add("A", AiActivityKind.Read, "First");
        now = now.AddMinutes(1);
        log.Add("A", AiActivityKind.Read, "Second");

        Assert.Equal(new[] { "Second", "First" }, log.Entries().Select(e => e.Request));
        log.Clear();
        Assert.Empty(log.Entries());
        Assert.Equal(3, changes);
    }

    [Fact]
    public void An_unreadable_file_is_an_empty_history()
    {
        string path = _folder.File("ai-activity.json");
        File.WriteAllText(path, "not json");
        var log = new AiActivityLog(path);

        log.Load();

        Assert.Empty(log.Entries());
    }

    [Fact]
    public void AI_access_is_off_in_new_and_older_settings_and_kept_once_on()
    {
        string path = _folder.File("settings.json");
        Assert.False(AppSettings.CreateDefault().AllowAiAccess);

        File.WriteAllText(path, """{ "SchemaVersion": 7, "Loops": [] }""");
        var store = new SettingsStore(path);
        AppSettings older = store.Load(out _);
        Assert.False(older.AllowAiAccess);

        older.AllowAiAccess = true;
        store.TrySave(older, out _);
        Assert.True(new SettingsStore(path).Load(out _).AllowAiAccess);
    }
}

public class AiCallLogTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static AiCall Call(int i) => new(new DateTime(2026, 10, 11, 14, 0, 0).AddSeconds(i), 1.5, "Claude Desktop", "patch_item", 120, 80, "ok", 3);

    [Fact]
    public void Every_call_is_one_line_with_its_times_and_sizes()
    {
        string path = _folder.File("logs/ai-calls.jsonl");
        var log = new AiCallLog(path);

        log.Add(Call(0));
        log.Add(Call(1) with { Tool = "get_item", Ops = null, Result = "error" });

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(new[] { "patch_item", "get_item" }, log.Entries().Select(c => c.Tool));
        Assert.Equal(3, log.Entries()[0].Ops);
        Assert.DoesNotContain("Ops", lines[1]);
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]); // no BOM
    }

    [Fact]
    public void The_oldest_lines_go_past_the_size_limit()
    {
        string path = _folder.File("ai-calls.jsonl");
        var log = new AiCallLog(path, maxBytes: 4000);

        for (int i = 0; i < 200; i++)
            log.Add(Call(i));

        AiCall[] entries = log.Entries();
        Assert.True(new FileInfo(path).Length <= 4000);
        Assert.InRange(entries.Length, 2, 199);
        Assert.Equal(Call(199).Start, entries[^1].Start);
    }

    [Fact]
    public void An_unreadable_line_is_skipped()
    {
        string path = _folder.File("ai-calls.jsonl");
        var log = new AiCallLog(path);
        log.Add(Call(0));
        File.AppendAllText(path, "not json\n");
        log.Add(Call(1));

        Assert.Equal(2, log.Entries().Length);
    }
}
