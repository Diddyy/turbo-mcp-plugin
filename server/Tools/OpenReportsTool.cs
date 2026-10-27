using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Moderation;
using Turbo.Primitives.Moderation.Snapshots;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools;

public sealed record OpenReportsInput(
    [Description("How many reports to answer, oldest first.")] [Range(1, 100)] int Take = 25
);

public sealed record OpenReportsOutput(int Open, IReadOnlyList<OpenReport> Reports);

public sealed record OpenReport(
    int Id,
    string State,
    int Priority,
    int AgeMinutes,
    int ReporterId,
    string Reporter,
    int ReportedId,
    string Reported,
    string? PickedBy,
    PlayerText Message
);

/// <summary>The moderation queue: the reports not yet closed, with who reported whom and what they wrote.</summary>
[RequiresPermission(McpNodes.OPEN_REPORTS)]
public sealed class OpenReportsTool(IModerationIssueService issues)
    : McpTool<OpenReportsInput, OpenReportsOutput>
{
    public override string Name => "open_reports";

    public override string Title => "Open reports";

    public override string Description =>
        "The moderation queue's open reports (calls for help), oldest first: id, open or picked (and by whom), priority, age in minutes, reporter, reported player and the reporter's message.";

    public override bool ReadOnly => true;

    public override async Task<McpToolResult> HandleAsync(
        OpenReportsInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var open = (await issues.GetOpenIssuesAsync(ct))
            .Where(x => x.State != ModIssueSnapshot.STATE_CLOSED)
            .OrderByDescending(x => x.IssueAgeInMilliseconds)
            .ToList();

        return Ok(
            new OpenReportsOutput(
                open.Count,
                [
                    .. open.Take(input.Take)
                        .Select(x => new OpenReport(
                            x.IssueId,
                            x.State == ModIssueSnapshot.STATE_PICKED ? "picked" : "open",
                            x.Priority,
                            x.IssueAgeInMilliseconds / 60_000,
                            x.ReporterUserId,
                            x.ReporterUserName,
                            x.ReportedUserId,
                            x.ReportedUserName,
                            x.State == ModIssueSnapshot.STATE_PICKED ? x.PickerUserName : null,
                            x.Message
                        )),
                ]
            ),
            open.Count == 0 ? "The queue is empty." : null
        );
    }
}
