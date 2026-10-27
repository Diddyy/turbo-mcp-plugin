using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Logging;
using Turbo.Primitives.Players.Permissions;
using Turbo.Primitives.Plugins;

namespace TurboMcp.Tools.Dev;

public sealed record PluginErrorsInput(
    [Description("The plugin's key (its manifest's Key).")]
    [StringLength(64, MinimumLength = 1)]
        string Key,
    [Description("How many of its recent warning and error log lines.")]
    [Range(0, 50)]
        int LogLines = 10
);

public sealed record PluginErrorsOutput(
    string Key,
    string State,
    string? Version,
    bool Dev,
    DateTime? LoadedAtUtc,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    string? Error,
    IReadOnlyList<PluginClientProblem> ClientProblems,
    IReadOnlyList<LogLine> Log
);

/// <summary>
/// One problem with a place: <see cref="Kind"/> <c>compile</c> (a C# error, <c>CS...</c>),
/// <c>refused</c> (a call plugins may not make, <c>TPP...</c>) or <c>client</c> (the client module's build).
/// </summary>
public sealed record PluginDiagnostic(
    string Kind,
    string File,
    int Line,
    int Column,
    string? Code,
    string Message
);

/// <summary>
/// For plugin authors: why a plugin did not load, as places to fix. The server writes a failed
/// compile as <c>Does not compile: server/X.cs(12,9): CS0103 ...; ...</c>, a refused one as
/// <c>Refused: ... TPP005 ...</c> and a client module that did not build as
/// <c>client/src/x.ts:3:7: ...</c> lines; each is answered as a file, line, column, code and message.
/// </summary>
[RequiresPermission(McpNodes.PLUGIN_ERRORS)]
public sealed partial class PluginErrorsTool(IPluginAdministration plugins, IServerLog log)
    : McpTool<PluginErrorsInput, PluginErrorsOutput>
{
    public override string Name => "plugin_errors";

    public override string Title => "A plugin's errors";

    public override string Description =>
        "One plugin's state on this hotel and every problem with a place: each C# compile error (CS...), refused call (TPP...) and client module build error as file, line, column, code and message, what clients report of its client half, and its recent warnings and errors from the server log (exceptions with stack traces). Use after saving a plugin's code on a dev hotel, or when it is Failed or Rejected.";

    public override bool ReadOnly => true;

    /// <summary>For a developer's own hotel: not registered on a live one.</summary>
    public override bool DevelopmentOnly => true;

    public override Task<McpToolResult> HandleAsync(
        PluginErrorsInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var all = plugins.List();
        var plugin = all.FirstOrDefault(x =>
            string.Equals(x.Key, input.Key.Trim(), StringComparison.OrdinalIgnoreCase)
        );

        if (plugin is null)
            return Task.FromResult(
                McpToolResult.Error(
                    $"No plugin has the key \"{input.Key}\". The keys are {string.Join(", ", all.Select(x => x.Key).Order())}."
                )
            );

        var problems =
            plugins
                .ContributionsOf(plugin.Key)
                ?.ClientIssues.Select(x => new PluginClientProblem(
                    x.Kind,
                    x.Subject,
                    x.Reason,
                    x.Message,
                    x.Clients
                ))
                .ToList()
            ?? [];

        return Task.FromResult(
            Ok(
                new PluginErrorsOutput(
                    plugin.Key,
                    plugin.State.ToString(),
                    plugin.Version,
                    plugin.Dev,
                    plugin.LoadedAtUtc,
                    Diagnostics(plugin.Error),
                    plugin.Error,
                    problems,
                    [.. LogOf(plugin, input.LogLines).Select(LogLine.Of)]
                ),
                plugin.State == PluginStateType.Loaded && plugin.Error is null
                    ? $"{plugin.Key} is loaded."
                    : null
            )
        );
    }

    /// <summary>Each place the error names, in its order.</summary>
    public static IReadOnlyList<PluginDiagnostic> Diagnostics(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return [];

        var found = new List<PluginDiagnostic>();

        foreach (Match m in CompilerError().Matches(error))
            found.Add(
                new PluginDiagnostic(
                    m.Groups["code"].Value.StartsWith("TPP", StringComparison.Ordinal)
                        ? "refused"
                        : "compile",
                    m.Groups["file"].Value,
                    int.Parse(m.Groups["line"].Value),
                    int.Parse(m.Groups["column"].Value),
                    m.Groups["code"].Value,
                    m.Groups["message"].Value.Trim()
                )
            );

        foreach (Match m in ClientError().Matches(error))
            found.Add(
                new PluginDiagnostic(
                    "client",
                    m.Groups["file"].Value,
                    int.Parse(m.Groups["line"].Value),
                    int.Parse(m.Groups["column"].Value),
                    null,
                    m.Groups["message"].Value.Trim()
                )
            );

        return found;
    }

    /// <summary>
    /// The plugin's warnings and errors still in the log: its own classes' lines (category
    /// <c>&lt;Name&gt;:...</c>) and the server's lines that name its key.
    /// </summary>
    private IEnumerable<ServerLogEntry> LogOf(PluginSnapshot plugin, int take) =>
        take == 0
            ? []
            : log.Recent(log.Capacity, LogLevel.Warning)
                .Where(x =>
                    (
                        plugin.Name is { } name
                        && x.Category.StartsWith($"{name}:", StringComparison.Ordinal)
                    )
                    || Names(x.Message, plugin.Key)
                    || (x.Exception is { } e && Names(e, plugin.Key))
                )
                .Take(take);

    private static bool Names(string text, string key) =>
        Regex.IsMatch(text, $@"(?<![\w.-]){Regex.Escape(key)}(?![\w-])", RegexOptions.IgnoreCase);

    // server/X.cs(12,9): CS0103 The name ...   (to the next "; <file>(", a new line or the end)
    [GeneratedRegex(
        @"(?<file>[\w./\\-]+\.cs)\((?<line>\d+),(?<column>\d+)\): (?<code>[A-Z]+\d+):? (?<message>.*?)(?=; [\w./\\-]+\.cs\(\d+,\d+\)|; |\r?\n| \(and \d+ more\)|$)",
        RegexOptions.Singleline
    )]
    private static partial Regex CompilerError();

    // client/src/x.ts:3:7: Could not resolve "y"
    [GeneratedRegex(
        @"(?<file>[\w./\\-]+\.(?:tsx?|jsx?|mjs|css)):(?<line>\d+):(?<column>\d+):? (?<message>[^\r\n]*)"
    )]
    private static partial Regex ClientError();
}
