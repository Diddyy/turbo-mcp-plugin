using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TurboMcp.Tools.Dev;

/// <summary>
/// The guides of the server this plugin runs on, and its AI kit's skills: the Markdown the server
/// ships in <c>plugin-sdk/docs</c> and <c>plugin-sdk/ai</c> beside its assemblies (turbo-cloud
/// docs/turbo-boost.md). Read once, as sections under their headings, and searched by the words
/// asked: no index service, nothing leaves the server.
/// </summary>
public sealed partial class DevDocs
{
    /// <summary>The most a <see cref="Read"/> answers, in characters.</summary>
    public const int MAX_READ_CHARS = 40_000;

    private readonly string _root;
    private readonly Lazy<IReadOnlyList<DocSection>> _sections;

    public DevDocs()
        : this(Path.Combine(AppContext.BaseDirectory, "plugin-sdk")) { }

    /// <summary>Over <paramref name="root"/>'s <c>docs/</c> and <c>ai/</c> (the tests' own folder).</summary>
    public DevDocs(string root)
    {
        _root = root;
        _sections = new Lazy<IReadOnlyList<DocSection>>(Load);
    }

    /// <summary>Whether the server ships its guides (a server built before the kit does not).</summary>
    public bool Available => _sections.Value.Count > 0;

    /// <summary>Every guide and skill, by path (<c>docs/plugin-wired.md</c>, <c>ai/skills/x/SKILL.md</c>).</summary>
    public IReadOnlyList<string> Paths => [.. _sections.Value.Select(x => x.Path).Distinct()];

    /// <summary>
    /// The sections that hold the most of <paramref name="query"/>'s words, best first: a word in a
    /// heading or the path counts more than one in the text, and a section with every word more
    /// than one with some.
    /// </summary>
    public IReadOnlyList<DocHit> Search(string query, int take)
    {
        var words = Words(query);

        if (words.Count == 0)
            return [];

        return
        [
            .. _sections
                .Value.Select(section => (Section: section, Score: Score(section, words)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Section.Path, StringComparer.Ordinal)
                .Take(take)
                .Select(x => new DocHit(
                    x.Section.Path,
                    x.Section.Heading,
                    x.Section.Line,
                    Math.Round(x.Score, 1),
                    Excerpt(x.Section, words)
                )),
        ];
    }

    /// <summary>
    /// The guide at <paramref name="path"/> (a path <see cref="Paths"/> lists, its file name, or a
    /// skill's name), or only its section whose heading holds <paramref name="heading"/>; null when
    /// there is none.
    /// </summary>
    public DocText? Read(string path, string? heading)
    {
        var wanted = path.Trim().Replace('\\', '/').TrimStart('/');
        var found = _sections
            .Value.Where(x =>
                string.Equals(x.Path, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileName(x.Path),
                    wanted,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    x.Path,
                    $"ai/skills/{wanted}/SKILL.md",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        if (found.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(heading))
        {
            var section = found.FirstOrDefault(x =>
                x.Heading.Contains(heading.Trim(), StringComparison.OrdinalIgnoreCase)
            );

            if (section is null)
                return null;

            found = [section];
        }

        var text = string.Join("\n", found.Select(x => x.Text));
        var cut = text.Length > MAX_READ_CHARS;

        return new DocText(
            found[0].Path,
            found.Count == 1 ? found[0].Heading : null,
            cut ? text[..MAX_READ_CHARS] : text,
            cut,
            [.. _sections.Value.Where(x => x.Path == found[0].Path).Select(x => x.Heading)]
        );
    }

    private IReadOnlyList<DocSection> Load()
    {
        var sections = new List<DocSection>();

        foreach (var folder in new[] { "docs", "ai" })
        {
            var at = Path.Combine(_root, folder);

            if (!Directory.Exists(at))
                continue;

            foreach (
                var file in Directory
                    .EnumerateFiles(at, "*.md", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal)
            )
                sections.AddRange(
                    Split(
                        Path.GetRelativePath(_root, file).Replace('\\', '/'),
                        File.ReadAllLines(file)
                    )
                );
        }

        return sections;
    }

    /// <summary>A file as its sections: each heading (<c>#</c> to <c>####</c>) starts one; code blocks are not headings.</summary>
    private static IEnumerable<DocSection> Split(string path, string[] lines)
    {
        var heading = Path.GetFileNameWithoutExtension(path);
        var start = 0;
        var fenced = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;

            if (fenced || HeadingLine().Match(lines[i]) is not { Success: true } match)
                continue;

            if (i > start)
                yield return new DocSection(
                    path,
                    heading,
                    start + 1,
                    string.Join('\n', lines[start..i])
                );

            heading = match.Groups[1].Value.Trim();
            start = i;
        }

        yield return new DocSection(path, heading, start + 1, string.Join('\n', lines[start..]));
    }

    private static double Score(DocSection section, IReadOnlyList<string> words)
    {
        var text = section.Text.ToLowerInvariant();
        var heading = section.Heading.ToLowerInvariant();
        var path = section.Path.ToLowerInvariant();
        double score = 0;
        var matched = 0;

        foreach (var word in words)
        {
            var count = Count(text, word);
            var inHeading = heading.Contains(word, StringComparison.Ordinal);
            var inPath = path.Contains(word, StringComparison.Ordinal);

            if (count == 0 && !inHeading && !inPath)
                continue;

            matched++;
            score += Math.Log(1 + count) + (inHeading ? 4 : 0) + (inPath ? 2 : 0);
        }

        // Every word in one section beats many hits of one word.
        return matched == 0 ? 0 : score * matched / words.Count * (matched == words.Count ? 2 : 1);
    }

    /// <summary>The lines around the line holding the most of the words, at most seven.</summary>
    private static string Excerpt(DocSection section, IReadOnlyList<string> words)
    {
        var lines = section.Text.Split('\n');
        var best = 0;
        var bestHits = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var lower = lines[i].ToLowerInvariant();
            var hits = words.Count(word => lower.Contains(word, StringComparison.Ordinal));

            if (hits > bestHits)
            {
                best = i;
                bestHits = hits;
            }
        }

        var from = Math.Max(0, best - 3);
        var excerpt = string.Join("\n", lines[from..Math.Min(lines.Length, best + 4)]).Trim();

        return excerpt.Length > 900 ? excerpt[..900] + " ..." : excerpt;
    }

    private static int Count(string text, string word)
    {
        var count = 0;

        for (
            var at = text.IndexOf(word, StringComparison.Ordinal);
            at >= 0;
            at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal)
        )
            count++;

        return count;
    }

    private static IReadOnlyList<string> Words(string query) =>
        [
            .. WordPattern()
                .Matches(query.ToLowerInvariant())
                .Select(m => m.Value)
                .Where(word => word.Length > 1 && !STOP.Contains(word))
                .Distinct(),
        ];

    private static readonly HashSet<string> STOP =
    [
        "a",
        "an",
        "and",
        "are",
        "do",
        "does",
        "for",
        "from",
        "how",
        "i",
        "in",
        "is",
        "it",
        "of",
        "on",
        "or",
        "the",
        "to",
        "what",
        "when",
        "where",
        "which",
        "with",
        "my",
        "can",
    ];

    [GeneratedRegex(@"^#{1,4}\s+(.+)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"[a-z0-9_.<>-]+")]
    private static partial Regex WordPattern();

    private sealed record DocSection(string Path, string Heading, int Line, string Text);
}

/// <summary>One section that holds the words: its file, heading, first line and the lines around the best match.</summary>
public sealed record DocHit(string Path, string Heading, int Line, double Score, string Excerpt);

/// <summary>A guide or one section of it; <see cref="Cut"/> when it was longer than the tool answers. <see cref="Headings"/>: every section of the file.</summary>
public sealed record DocText(
    string Path,
    string? Heading,
    string Text,
    bool Cut,
    IReadOnlyList<string> Headings
);
