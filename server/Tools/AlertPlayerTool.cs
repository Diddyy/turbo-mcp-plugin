using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools;

public sealed record AlertPlayerInput(
    [Description("The online player's id.")] [Range(1, int.MaxValue)] int PlayerId,
    [Description("The message staff will approve and the player will see.")]
    [StringLength(2000, MinimumLength = 1)]
        string Message
);

/// <summary>Sends an alert after a staff member approves the exact message.</summary>
[RequiresPermission(McpNodes.ALERT_PLAYER)]
public sealed class AlertPlayerTool(IHotel hotel) : McpTool<AlertPlayerInput>
{
    public override string Name => "alert_player";
    public override string Description =>
        "Send an alert to an online player. Every call waits for another staff member's approval; pending means nothing was sent yet.";
    public override bool RequiresApproval => true;
    public override string ApprovalNode => McpNodes.APPROVE_ACTIONS;

    public override async Task<McpToolResult> HandleAsync(
        AlertPlayerInput input,
        McpToolContext ctx,
        CancellationToken ct
    ) =>
        await hotel.Notify.AlertAsync(input.PlayerId, "mcp.staff.alert", "%0%", [input.Message], ct)
            ? McpToolResult.Text($"Alert sent to player {input.PlayerId}.")
            : McpToolResult.Error("The player is offline. No alert was sent.");
}
