using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Orleans;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools;

public sealed record PlayerDetailsInput(
    [Description("The player's id (find_player gives it).")] [Range(1, int.MaxValue)] int Id,
    [Description("How many kinds of furni to list, most held first.")]
    [Range(0, 100)]
        int FurniKinds = 20
);

public sealed record PlayerDetailsOutput(
    int Id,
    string Name,
    PlayerText Motto,
    bool Online,
    RoomRef? CurrentRoom,
    bool Club,
    int ClubStreakDays,
    double HoursOnline,
    IReadOnlyDictionary<string, int> Wallet,
    int FurniTotal,
    IReadOnlyList<FurniCount> Furni
);

public sealed record RoomRef(int Id, PlayerText Name);

public sealed record FurniCount(string Name, int Count);

/// <summary>A player's wallet, club, current room, time online and the furni they hold.</summary>
[RequiresPermission(McpNodes.PLAYER_DETAILS)]
public sealed class PlayerDetailsTool(IHotel hotel, IGrainFactory grains)
    : McpTool<PlayerDetailsInput, PlayerDetailsOutput>
{
    /// <summary>The hotel's currencies every hotel has.</summary>
    private static readonly string[] CURRENCIES = ["credits", "duckets", "diamonds"];

    public override string Name => "player_details";

    public override string Title => "Player details";

    public override string Description =>
        "One player's wallet (credits, duckets, diamonds), club, the room they are in, hours online and the furni in their inventory counted by kind. Takes the id find_player gives.";

    public override bool ReadOnly => true;

    public override async Task<McpToolResult> HandleAsync(
        PlayerDetailsInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        if (await hotel.Players.GetAsync(input.Id, ct) is not { } player)
            return McpToolResult.Error(
                $"There is no player with the id {input.Id}. find_player finds one by name."
            );

        var online = hotel.Players.OnlineIds().Contains(player.Id);
        var room = online ? await hotel.Players.CurrentRoomAsync(player.Id, ct) : null;
        var roomInfo = room is { } r ? await hotel.Rooms.GetAsync(r, ct) : null;
        var club = await hotel.Players.ClubAsync(player.Id, ct);
        var time = await hotel.Players.OnlineTimeAsync(player.Id, ct);
        var wallet = new Dictionary<string, int>();

        foreach (var currency in CURRENCIES.Where(hotel.Wallet.CurrencyExists))
            wallet[currency] = await hotel.Wallet.BalanceAsync(player.Id, currency, ct);

        var items = await grains.GetInventoryGrain(player.Id).GetAllItemSnapshotsAsync(ct);
        var kinds = items
            .GroupBy(x => x.Definition.Name)
            .Select(x => new FurniCount(x.Key, x.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(input.FurniKinds)
            .ToList();

        return Ok(
            new PlayerDetailsOutput(
                player.Id.Value,
                player.Name,
                player.Motto,
                online,
                room is { } at ? new RoomRef(at.Value, roomInfo?.Name ?? string.Empty) : null,
                club.Active,
                club.StreakDays,
                Math.Round((time?.Total ?? TimeSpan.Zero).TotalHours, 1),
                wallet,
                items.Length,
                kinds
            )
        );
    }
}
