using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuppyMacro.Resources;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// Adds PuppyMacro to the AI apps' MCP settings and tells whether it is there: Claude Desktop
/// (claude_desktop_config.json), Visual Studio Code (its install link; mcp.json) and Codex (config.toml). The entry
/// starts <c>PuppyMacro.exe --mcp</c> (<see cref="AiBridge"/>). File paths are parameters, so the unit tests use their own.
/// </summary>
internal static class AiApps
{
    /// <summary>The name of PuppyMacro's entry. The development build has its own, so it never replaces the installed one.</summary>
    public static string ServerName { get; } = App.IsDevBuild ? "puppymacro-dev" : "puppymacro";

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The configuration for any other app (Copy configuration): the usual "mcpServers" shape.</summary>
    public static string GenericConfiguration(string exe)
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [ServerName] = Entry(exe) },
        };
        return root.ToJsonString(Indented);
    }

    private static JsonObject Entry(string exe) => new()
    {
        ["command"] = exe,
        ["args"] = new JsonArray("--mcp"),
    };

    // ================= Claude Desktop =================

    /// <summary>
    /// The claude_desktop_config.json Claude Desktop reads: installed from the Microsoft Store (MSIX) it reads its own copy
    /// under %LocalAppData%\Packages\Claude_…\LocalCache\Roaming\Claude when that file exists, otherwise the one in
    /// %AppData%\Claude.
    /// </summary>
    public static string ClaudeConfigPath(string appData, string localAppData)
    {
        string packages = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (string package in Directory.GetDirectories(packages, "Claude_*"))
            {
                string virtualized = Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
                if (File.Exists(virtualized))
                    return virtualized;
            }
        }
        return Path.Combine(appData, "Claude", "claude_desktop_config.json");
    }

    /// <summary>Claude Desktop is installed: its data folder exists (it creates it on first start).</summary>
    public static bool ClaudeInstalled(string appData, string localAppData) =>
        Directory.Exists(Path.Combine(appData, "Claude"))
        || (Directory.Exists(Path.Combine(localAppData, "Packages"))
            && Directory.GetDirectories(Path.Combine(localAppData, "Packages"), "Claude_*").Length > 0);

    /// <summary>The file has PuppyMacro's entry with this exe.</summary>
    public static bool ClaudeHas(string configPath, string exe) => HasEntry(configPath, "mcpServers", exe);

    /// <summary>Adds (or updates) PuppyMacro's entry, keeping everything else. Null when done, otherwise why not.</summary>
    public static string? ClaudeAdd(string configPath, string exe) =>
        EditJson(configPath, "Claude Desktop", "mcpServers", servers => servers[ServerName] = Entry(exe));

    public static string? ClaudeRemove(string configPath) =>
        EditJson(configPath, "Claude Desktop", "mcpServers", servers => servers.Remove(ServerName));

    // ================= Visual Studio Code =================

    /// <summary>The user's mcp.json of the default profile (%AppData%\Code\User\mcp.json).</summary>
    public static string VsCodeConfigPath(string appData) => Path.Combine(appData, "Code", "User", "mcp.json");

    /// <summary>Code.exe, installed for the user or for everyone; null when not found.</summary>
    public static string? VsCodeExe(string localAppData, string programFiles) =>
        new[]
        {
            Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
        }.FirstOrDefault(File.Exists);

    public static bool VsCodeHas(string configPath, string exe) => HasEntry(configPath, "servers", exe);

    /// <summary>
    /// VS Code's link that opens its "install MCP server" page with PuppyMacro filled in: vscode:mcp/install?{json}. The
    /// user selects Install there.
    /// </summary>
    public static string VsCodeInstallUri(string exe)
    {
        var entry = new JsonObject
        {
            ["name"] = ServerName,
            ["command"] = exe,
            ["args"] = new JsonArray("--mcp"),
        };
        return "vscode:mcp/install?" + Uri.EscapeDataString(entry.ToJsonString());
    }

    /// <summary>
    /// Removes PuppyMacro from mcp.json, only when the file has no comments (rewriting it would lose them): otherwise the
    /// answer says how to remove it in VS Code.
    /// </summary>
    public static string? VsCodeRemove(string configPath)
    {
        if (File.Exists(configPath))
        {
            try
            {
                using var strict = JsonDocument.Parse(File.ReadAllText(configPath));
            }
            catch (JsonException)
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_VsCodeRemoveByHand, ServerName);
            }
        }
        return EditJson(configPath, "Visual Studio Code", "servers", servers => servers.Remove(ServerName));
    }

    // ================= Codex =================

    /// <summary>%UserProfile%\.codex\config.toml (Codex's app, command line and editor extension share it).</summary>
    public static string CodexConfigPath(string userProfile) => Path.Combine(userProfile, ".codex", "config.toml");

    /// <summary>Codex was used on this PC: it creates its .codex folder.</summary>
    public static bool CodexInstalled(string userProfile) => Directory.Exists(Path.Combine(userProfile, ".codex"));

    private static string CodexHeader => $"[mcp_servers.{ServerName}]";

    public static bool CodexHas(string configPath, string exe)
    {
        if (!File.Exists(configPath))
            return false;
        List<string> block = CodexBlock(File.ReadAllLines(configPath)).Lines;
        return block.Count > 0 && block.Any(l => l.Trim().StartsWith("command", StringComparison.Ordinal)
            && l.Contains(exe, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds (or replaces) PuppyMacro's table. tool_timeout_sec is longer than Codex's default, as PuppyMacro waits for the
    /// user (and answers "waiting" after about 40 seconds anyway).
    /// </summary>
    public static string? CodexAdd(string configPath, string exe)
    {
        if (exe.Contains('\'') || exe.Contains('\n'))
            return Strings.AiApps_CodexBadFolder;
        try
        {
            List<string> lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new();
            lines = CodexBlock(lines).Without;
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0)
                lines.Add("");
            lines.Add(CodexHeader);
            lines.Add($"command = '{exe}'");
            lines.Add("args = [\"--mcp\"]");
            lines.Add("tool_timeout_sec = 600");
            Write(configPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_SettingsNotChanged, "Codex", ex.Message);
        }
    }

    public static string? CodexRemove(string configPath)
    {
        if (!File.Exists(configPath))
            return null;
        try
        {
            List<string> lines = CodexBlock(File.ReadAllLines(configPath)).Without;
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);
            Write(configPath, lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_SettingsNotChanged, "Codex", ex.Message);
        }
    }

    /// <summary>
    /// PuppyMacro's table (and its sub-tables, like .env) and the other lines. A header is recognized however it is
    /// written (<c>[ mcp_servers."puppymacro" ] # note</c>). Comments and blank lines just before the next table belong
    /// to that table, so they stay.
    /// </summary>
    private static (List<string> Lines, List<string> Without) CodexBlock(IEnumerable<string> all)
    {
        var block = new List<string>();
        var without = new List<string>();
        var loose = new List<string>(); // comments and blank lines inside our table: ours unless a table follows
        bool inside = false;
        string ours = "mcp_servers." + ServerName;
        foreach (string line in all)
        {
            string trimmed = line.Trim();
            if (TableName(trimmed) is string table)
            {
                without.AddRange(loose);
                loose.Clear();
                inside = table == ours || table.StartsWith(ours + ".", StringComparison.Ordinal);
            }
            else if (inside && (trimmed.Length == 0 || trimmed.StartsWith('#')))
            {
                loose.Add(line);
                continue;
            }
            if (inside)
            {
                block.AddRange(loose);
                loose.Clear();
            }
            (inside ? block : without).Add(line);
        }
        block.AddRange(loose);
        return (block, without);
    }

    /// <summary>"mcp_servers.puppymacro" for a table header line (spaces and quotes taken out), null for any other line.</summary>
    private static string? TableName(string trimmed)
    {
        if (!trimmed.StartsWith('[') || trimmed.StartsWith("[[", StringComparison.Ordinal))
            return null;
        int end = trimmed.IndexOf(']');
        if (end < 0)
            return null;
        return string.Join(".", trimmed[1..end].Split('.').Select(part => part.Trim().Trim('"', '\'')));
    }

    // ================= JSON files =================

    /// <summary>
    /// The file has PuppyMacro's entry under <paramref name="key"/> with this exe. Whatever the file holds (it belongs to
    /// another app: an array where an object is expected, a number as the command, a key twice), the answer is a plain no.
    /// </summary>
    private static bool HasEntry(string path, string key, string exe)
    {
        try
        {
            return File.Exists(path)
                && JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is JsonObject root
                && root[key] is JsonObject servers
                && servers[ServerName] is JsonObject entry
                && entry["command"] is JsonValue command
                && command.TryGetValue(out string? text)
                && string.Equals(text, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Changes the object under <paramref name="key"/> of a JSON settings file, keeping the rest; never replaces a file
    /// that can't be read or whose <paramref name="key"/> is not an object.
    /// </summary>
    private static string? EditJson(string path, string appName, string key, Action<JsonObject> change)
    {
        try
        {
            JsonObject root;
            if (File.Exists(path) && File.ReadAllText(path).Trim().Length > 0)
            {
                if (JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is not JsonObject existing)
                    return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_SettingsNotObject, appName, path);
                root = existing;
            }
            else
            {
                root = new JsonObject();
            }
            if (root[key] is null)
                root[key] = new JsonObject();
            if (root[key] is not JsonObject servers)
                return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_SettingsKeyNotObject, appName, key, path);
            change(servers);
            Write(path, root.ToJsonString(Indented));
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_SettingsFileError, appName, ex.Message, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_SettingsNotChanged, appName, ex.Message);
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
