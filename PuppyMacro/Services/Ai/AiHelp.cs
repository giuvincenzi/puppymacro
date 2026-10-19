using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The user guide of this version (site/guide, embedded at build time) as plain text, for the get_help tool: the AI
/// answers "how do I…" from the guide, not from what it imagines.
/// </summary>
internal static class AiHelp
{
    private const string Prefix = "PuppyMacro.Guide.";

    /// <summary>A guide section: its file name without .html ("remap") and its title ("Remap").</summary>
    public sealed record Section(string Id, string Title);

    /// <summary>The sections in the guide's order (site/guide/sections.txt).</summary>
    public static IReadOnlyList<Section> Sections { get; } = ReadSections();

    /// <summary>A section's text, or null for an unknown id.</summary>
    public static string? Text(string id)
    {
        Section? section = Sections.FirstOrDefault(s => string.Equals(s.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (section == null)
            return null;
        string? html = Resource(section.Id + ".html");
        return html == null ? null : $"# {section.Title}\n\n{ToText(html)}";
    }

    /// <summary>The list of sections, for get_help without a topic.</summary>
    public static string Index() =>
        "PuppyMacro user guide. Call get_help with one of these topics:\n" +
        string.Join("\n", Sections.Select(s => $"- {s.Id}: {s.Title}"));

    private static List<Section> ReadSections()
    {
        string? text = Resource("sections.txt");
        if (text == null)
            return new();
        return text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new Section(parts[0].Trim(), parts[1].Trim()))
            .ToList();
    }

    private static string? Resource(string name)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name);
        if (stream == null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The guide's simple HTML (paragraphs, lists, headings, strong, kbd, links, figures) as readable text.</summary>
    internal static string ToText(string html)
    {
        string text = Regex.Replace(Drawings(html), @"<figure[\s\S]*?</figure>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<h([1-6])[^>]*>", m => "\n" + new string('#', int.Parse(m.Groups[1].Value)) + " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|/p|/ul|/ol|/li|/table|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</t[dh]>", " | ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static readonly Regex DrawingStart = new(@"<div\b[^>]*\brole=""img""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The guide's pictures drawn in HTML (a <c>div</c> with <c>role="img"</c>, as the overlay panel's) as what they show, their
    /// <c>aria-label</c>, in a paragraph of their own: their texts, read one after the other, made no sentence.
    /// </summary>
    private static string Drawings(string html)
    {
        var text = new System.Text.StringBuilder();
        int at = 0;
        for (Match start = DrawingStart.Match(html); start.Success; start = DrawingStart.Match(html, at))
        {
            text.Append(html, at, start.Index - at);
            // Its end: the </div> that closes it, past the divs inside.
            int depth = 1, i = start.Index + start.Length;
            var tags = new Regex(@"<(/?)div\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (Match tag = tags.Match(html, i); tag.Success && depth > 0; tag = tag.NextMatch())
            {
                depth += tag.Groups[1].Value.Length == 0 ? 1 : -1;
                i = tag.Index + tag.Length;
            }
            string label = Regex.Match(start.Value, @"aria-label=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            text.Append("<p>(Picture: ").Append(label).Append(")</p>");
            at = i;
        }
        text.Append(html, at, html.Length - at);
        return text.ToString();
    }
}
