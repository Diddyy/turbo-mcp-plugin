using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;
using Turbo.Primitives.Plugins;

namespace TurboMcp.Tools;

public sealed record PluginStatesInput(
    [Description(
        "A plugin's key, for what it contributes and what clients report of it. Leave it out for every plugin's state."
    )]
        string? Key = null
);

public sealed record PluginStatesOutput(IReadOnlyList<PluginState> Plugins, PluginDetail? Detail);

public sealed record PluginState(
    string Key,
    string? Name,
    string? Version,
    string State,
    string? Error,
    bool Dev,
    DateTime? LoadedAtUtc
);

public sealed record PluginDetail(
    string Key,
    IReadOnlyList<PluginContributionItem> Contributions,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<PluginClientProblem> ClientProblems
);

public sealed record PluginContributionItem(string Kind, string Name, string? Detail);

public sealed record PluginClientProblem(
    string Kind,
    string Subject,
    string Reason,
    string Message,
    int Clients
);

/// <summary>For plugin authors: each plugin's state and error (a compile error or a refused call with its file and line), and one plugin's contributions.</summary>
[RequiresPermission(McpNodes.PLUGIN_STATES)]
public sealed class PluginStatesTool(IPluginAdministration plugins)
    : McpTool<PluginStatesInput, PluginStatesOutput>
{
    public override string Name => "plugin_states";

    public override string Title => "Plugin states";

    public override string Description =>
        "Every plugin's key, version and state (Loaded, Failed, Rejected, Disabled, NotLoaded) with its error: a compile error or a refused call (TPP rules) with its file and line, or what it threw as it started. Given a key, also what that plugin contributes (commands, routes, tools, patches...), its conflicts and the problems clients report with its client half.";

    public override bool ReadOnly => true;

    public override Task<McpToolResult> HandleAsync(
        PluginStatesInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var list = plugins
            .List()
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new PluginState(
                x.Key,
                x.Name,
                x.Version,
                x.State.ToString(),
                x.Error,
                x.Dev,
                x.LoadedAtUtc
            ))
            .ToList();
        PluginDetail? detail = null;

        if (!string.IsNullOrWhiteSpace(input.Key))
        {
            if (plugins.ContributionsOf(input.Key.Trim()) is not { } of)
                return Task.FromResult(
                    McpToolResult.Error(
                        $"No plugin has the key \"{input.Key}\". The keys are {string.Join(", ", list.Select(x => x.Key))}."
                    )
                );

            detail = new PluginDetail(
                of.Key,
                [.. of.Items.Select(x => new PluginContributionItem(x.Kind, x.Name, x.Detail))],
                [.. of.Conflicts],
                [
                    .. of.ClientIssues.Select(x => new PluginClientProblem(
                        x.Kind,
                        x.Subject,
                        x.Reason,
                        x.Message,
                        x.Clients
                    )),
                ]
            );
        }

        return Task.FromResult(Ok(new PluginStatesOutput(list, detail)));
    }
}
