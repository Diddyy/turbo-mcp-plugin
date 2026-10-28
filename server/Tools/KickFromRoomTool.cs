using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools;

public sealed record KickFromRoomInput(
    [Description("The room's id.")] [Range(1, int.MaxValue)] int RoomId,
    [Description("The player's id.")] [Range(1, int.MaxValue)] int PlayerId
);

/// <summary>Kicks through the hotel's room permission and presence rules after approval.</summary>
[RequiresPermission(McpNodes.KICK_FROM_ROOM)]
public sealed class KickFromRoomTool(IHotel hotel) : McpTool<KickFromRoomInput>
{
    public override string Name => "kick_from_room";
    public override string Description =>
        "Kick a player from a room as the staff member whose token this is. Every call waits for another staff member's approval; room rights and kick immunity still apply.";
    public override bool RequiresApproval => true;
    public override string ApprovalNode => McpNodes.APPROVE_ACTIONS;

    public override async Task<McpToolResult> HandleAsync(
        KickFromRoomInput input,
        McpToolContext ctx,
        CancellationToken ct
    ) =>
        await hotel.Rooms.KickAsync(input.RoomId, input.PlayerId, ctx.Caller, ct)
            ? McpToolResult.Text($"Player {input.PlayerId} was kicked from room {input.RoomId}.")
            : McpToolResult.Error(
                "The player could not be kicked: check their room, your room rights and their kick immunity."
            );
}
