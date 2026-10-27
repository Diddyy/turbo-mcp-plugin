using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Action;
using Turbo.Primitives.Orleans;
using Turbo.Primitives.Players.Permissions;
using Turbo.Primitives.Rooms;

namespace TurboMcp.Tools;

public sealed record RoomStateInput(
    [Description("The room's id.")] [Range(1, int.MaxValue)] int Id
);

public sealed record RoomStateOutput(
    int Id,
    PlayerText Name,
    PlayerText Description,
    int OwnerId,
    string OwnerName,
    bool Public,
    bool Loaded,
    IReadOnlyList<RoomUser> Users,
    RoomLoad? Load,
    IReadOnlyList<WiredError> WiredErrors
);

public sealed record RoomUser(int Id, string Name);

public sealed record RoomLoad(
    int FloorItems,
    int FloorItemCap,
    int WallItems,
    int WallItemCap,
    double WiredExecutionCost,
    double WiredExecutionCostCap,
    bool WiredHeavy
);

public sealed record WiredError(string Error, string Category, int Count, long? SecondsSinceLast);

/// <summary>
/// A room: its owner and users, and while it is loaded its furni counts, wired load and the wired
/// monitor's errors. A room nobody is in is not loaded for this: its furni and wired read empty.
/// </summary>
[RequiresPermission(McpNodes.ROOM_STATE)]
public sealed class RoomStateTool(IHotel hotel, IGrainFactory grains)
    : McpTool<RoomStateInput, RoomStateOutput>
{
    public override string Name => "room_state";

    public override string Title => "Room state";

    public override string Description =>
        "A room by id: name, owner, whether public, the users in it, and while it is loaded (someone is in it) its floor and wall furni counts against the caps, its wired load and the wired monitor's error list (each error, how often, seconds since it last happened). Use it to look into a room's wired problems.";

    public override bool ReadOnly => true;

    public override async Task<McpToolResult> HandleAsync(
        RoomStateInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var id = new RoomId(input.Id);

        if (await hotel.Rooms.GetAsync(id, ct) is not { } room)
            return McpToolResult.Error($"There is no room with the id {input.Id}.");

        // Reading a room that is not loaded would load it: only a loaded room is asked.
        var loaded = (await grains.GetRoomDirectoryGrain().GetActiveRoomIdsAsync(ct)).Contains(id);
        var players = loaded ? await hotel.Rooms.PlayersAsync(id, ct) : [];
        var names = players.Count > 0 ? await hotel.Players.NamesAsync(players, ct) : null;
        RoomLoad? load = null;
        List<WiredError> errors = [];

        if (loaded)
        {
            var grain = grains.GetRoomGrain(id);
            var system = ActionContext.CreateForSystem(id);

            if (await grain.GetWiredRoomStatsAsync(system, ct) is { } stats)
                load = new RoomLoad(
                    stats.FloorItemCount,
                    stats.FloorItemCap,
                    stats.WallItemCount,
                    stats.WallItemCap,
                    stats.ExecutionCost,
                    stats.ExecutionCostCap,
                    stats.IsHeavy
                );

            if (await grain.GetWiredErrorLogsAsync(system, ct) is { } logs)
                errors =
                [
                    .. logs.Where(x => x.ThrowCount > 0)
                        .Select(x => new WiredError(
                            x.ErrorName,
                            x.Category,
                            x.ThrowCount,
                            x.MsSinceLastOccurrence < 0 ? null : x.MsSinceLastOccurrence / 1000
                        )),
                ];
        }

        return Ok(
            new RoomStateOutput(
                room.Id.Value,
                room.Name,
                room.Description,
                room.Owner.Value,
                room.OwnerName,
                room.IsPublic,
                loaded,
                [
                    .. players.Select(x => new RoomUser(
                        x.Value,
                        names?.GetValueOrDefault(x) ?? $"#{x.Value}"
                    )),
                ],
                load,
                errors
            ),
            loaded
                ? null
                : "The room is not loaded (nobody is in it): its furni and wired are read only while it is."
        );
    }
}
