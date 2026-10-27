using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools;

public sealed record FindPlayerInput(
    [Description("The start of the player's name, in any case. Leave it out to look up an id.")]
        string? Name = null,
    [Description("A player's id, when it is known.")] int? Id = null,
    [Description("How many players to answer.")] [Range(1, 50)] int Take = 10
);

public sealed record FindPlayerOutput(IReadOnlyList<FoundPlayer> Players);

public sealed record FoundPlayer(
    int Id,
    string Name,
    PlayerText Motto,
    bool Online,
    DateTime CreatedAt,
    DateTime? LastLoginAt
);

/// <summary>Players by the start of their name, or by id: who they are and whether online.</summary>
[RequiresPermission(McpNodes.FIND_PLAYER)]
public sealed class FindPlayerTool(IHotel hotel) : McpTool<FindPlayerInput, FindPlayerOutput>
{
    public override string Name => "find_player";

    public override string Title => "Find a player";

    public override string Description =>
        "Finds players whose name starts with the given text (or the player with the given id): id, name, motto, whether online, when they joined and last logged in. Use it first to get the id other player tools take.";

    public override bool ReadOnly => true;

    public override async Task<McpToolResult> HandleAsync(
        FindPlayerInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        if (input.Id is null && string.IsNullOrWhiteSpace(input.Name))
            return McpToolResult.Error("Give a name (or the start of one) or an id.");

        var found = input.Id is { } id
            ? (await hotel.Players.GetAsync(id, ct) is { } player ? [player] : [])
            : await hotel.Players.SearchAsync(input.Name!.Trim(), input.Take, ct);

        if (found.Count == 0)
            return McpToolResult.Error(
                input.Id is { } missing
                    ? $"There is no player with the id {missing}."
                    : $"No player's name starts with \"{input.Name}\". Try fewer letters."
            );

        var online = hotel.Players.OnlineIds();

        return Ok(
            new FindPlayerOutput([
                .. found.Select(x => new FoundPlayer(
                    x.Id.Value,
                    x.Name,
                    x.Motto,
                    online.Contains(x.Id),
                    x.CreatedAt,
                    x.LastLoginAt
                )),
            ])
        );
    }
}
