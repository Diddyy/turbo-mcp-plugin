using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;
using Turbo.Primitives.Plugins;
using Turbo.Primitives.Rooms;
using TurboMcp.Tools;

namespace TurboMcp.Resources;

public sealed record RoomArgs([Description("The room's id.")] int RoomId);

/// <summary>A room as <see cref="RoomStateTool"/> answers it, read by its address.</summary>
[RequiresPermission(McpNodes.ROOM_RESOURCE)]
public sealed class RoomResource(IHotel hotel, IGrainFactory grains) : McpResourceTemplate<RoomArgs>
{
    public override string UriTemplate => "turbo://rooms/{roomId}";

    public override string Name => "room";

    public override string Title => "Room";

    public override string Description =>
        "A room by id: name, owner, whether public, the users in it, and while it is loaded its furni counts against the caps, its wired load and the wired monitor's errors.";

    public override async Task<McpResourceResult> HandleAsync(
        RoomArgs arguments,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var state = await new RoomStateTool(hotel, grains).HandleAsync(
            new RoomStateInput(arguments.RoomId),
            ctx,
            ct
        );

        return state.Value is { } room
            ? McpResourceResult.Data(room)
            : McpResourceResult.NotFound(state.Message ?? $"There is no room {arguments.RoomId}.");
    }
}

public sealed record PlayerArgs([Description("The player's id.")] int PlayerId);

/// <summary>What any player could see of another in the hotel: never their wallet, inventory or anything private.</summary>
public sealed record PlayerProfile(
    int Id,
    PlayerText Name,
    PlayerText Motto,
    string Figure,
    bool Club,
    DateTime JoinedAtUtc
);

/// <summary>A player's profile: what the hotel shows of them, no wallet, inventory or anything private.</summary>
[RequiresPermission(McpNodes.PLAYER_RESOURCE)]
public sealed class PlayerResource(IHotel hotel) : McpResourceTemplate<PlayerArgs>
{
    public override string UriTemplate => "turbo://players/{playerId}";

    public override string Name => "player";

    public override string Title => "Player";

    public override string Description =>
        "A player's public profile by id: name, motto, figure, club and when they joined. No online status, room location, last login, wallet, inventory or private account data.";

    public override async Task<McpResourceResult> HandleAsync(
        PlayerArgs arguments,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        if (await hotel.Players.GetAsync(arguments.PlayerId, ct) is not { } player)
            return McpResourceResult.NotFound(
                $"There is no player with the id {arguments.PlayerId}. find_player finds one by name."
            );

        return McpResourceResult.Data(
            new PlayerProfile(
                player.Id.Value,
                player.Name,
                player.Motto,
                player.Figure,
                (await hotel.Players.ClubAsync(player.Id, ct)).Active,
                player.CreatedAt
            )
        );
    }
}

public sealed record PluginArgs([Description("The plugin's key.")] string Key);

public sealed record PluginView(PluginState State, PluginDetail Detail);

/// <summary>One plugin as <see cref="PluginStatesTool"/> answers it: its state and error, contributions and clients' problems.</summary>
[RequiresPermission(McpNodes.PLUGIN_RESOURCE)]
public sealed class PluginResource(IPluginAdministration plugins) : McpResourceTemplate<PluginArgs>
{
    public override string UriTemplate => "turbo://plugins/{key}";

    public override string Name => "plugin";

    public override string Title => "Plugin";

    public override string Description =>
        "A plugin by key: its version, state and error (a compile error or a refused call with file and line), what it contributes, its conflicts and the problems clients report with its client half.";

    public override async Task<McpResourceResult> HandleAsync(
        PluginArgs arguments,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var states = await new PluginStatesTool(plugins).HandleAsync(
            new PluginStatesInput(arguments.Key),
            ctx,
            ct
        );

        if (states.Value is not PluginStatesOutput { Detail: { } detail } found)
            return McpResourceResult.NotFound(
                states.Message ?? $"No plugin has the key \"{arguments.Key}\"."
            );

        return McpResourceResult.Data(
            new PluginView(
                found.Plugins.Single(x =>
                    string.Equals(x.Key, detail.Key, StringComparison.OrdinalIgnoreCase)
                ),
                detail
            )
        );
    }
}
