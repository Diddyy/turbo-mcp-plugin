using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Admin;

namespace TurboMcp;

/// <summary>
/// The panel's MCP pages: a member makes, sees and revokes their own tokens (and reads how to
/// connect an assistant); staff managing MCP see and revoke everyone's and read the call log.
/// </summary>
public sealed class McpAdmin(McpTokens tokens, McpAudit audit, IHotel hotel, IConfiguration config)
    : IPluginAdmin
{
    private const int PAGE_SIZE = 25;

    public void Configure(IPluginAdminBuilder admin)
    {
        var use = admin.Node(
            "use",
            "MCP: make and use your own tokens, letting an AI assistant act as you"
        );
        var manage = admin.Node(
            "manage",
            "MCP: see and revoke every staff member's tokens, and read the call log"
        );

        foreach (var (name, description) in McpNodes.NODES)
            admin.Node(name, description);

        admin.Map("GET", "tokens", use, ListTokensAsync);
        admin.Map("POST", "tokens", use, CreateAsync);
        admin.Map("GET", "tokens/{id}", use, GetTokenAsync);
        admin.Map("DELETE", "tokens/{id}", use, RevokeAsync);
        admin.Map("GET", "calls", manage, ListCallsAsync);

        admin.Page(
            new PluginAdminPage(
                "tokens",
                "MCP",
                use,
                [
                    new PluginAdminBlock("text", "Connect your AI", Text: ConnectText()),
                    new PluginAdminBlock(
                        "form",
                        "New token",
                        Fields:
                        [
                            new PluginAdminField(
                                "label",
                                "Name",
                                Required: true,
                                Hint: "Where it is used, such as Claude Code on my laptop"
                            ),
                            new PluginAdminField(
                                "readOnly",
                                "Read-only",
                                "bool",
                                Hint: "Only tools that change nothing"
                            ),
                            new PluginAdminField(
                                "days",
                                "Days it lasts",
                                "number",
                                Min: 1,
                                Max: 365
                            ),
                        ],
                        Submit: new PluginAdminAction(
                            "Make token",
                            "POST",
                            "tokens",
                            Then: "open:token"
                        )
                    ),
                    new PluginAdminBlock(
                        "table",
                        "Tokens",
                        Endpoint: "tokens",
                        Columns:
                        [
                            new PluginAdminColumn("label", "Name"),
                            new PluginAdminColumn("owner", "Staff member"),
                            new PluginAdminColumn("prefix", "Starts"),
                            new PluginAdminColumn("readOnly", "Read-only", "bool"),
                            new PluginAdminColumn("state", "State", "badge"),
                            new PluginAdminColumn("expiresAt", "Expires", "time"),
                            new PluginAdminColumn("lastUsedAt", "Last used", "time"),
                        ],
                        ItemPage: "token"
                    ),
                ],
                "Tokens that let an AI assistant use the hotel's tools as you.",
                Icon: "bot"
            )
        );
        admin.Page(
            new PluginAdminPage(
                "token",
                "MCP token",
                use,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "tokens/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("label", "Name"),
                            new PluginAdminColumn("token", "Token"),
                            new PluginAdminColumn("owner", "Staff member"),
                            new PluginAdminColumn("readOnly", "Read-only", "bool"),
                            new PluginAdminColumn("state", "State", "badge"),
                            new PluginAdminColumn("createdAt", "Made", "time"),
                            new PluginAdminColumn("expiresAt", "Expires", "time"),
                            new PluginAdminColumn("lastUsedAt", "Last used", "time"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Revoke",
                                "DELETE",
                                "tokens/{id}",
                                Confirm: "Revoke this token? An assistant using it stops at once.",
                                Danger: true,
                                Then: "back",
                                When: "state=active"
                            ),
                        ]
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "calls",
                "MCP calls",
                manage,
                [
                    new PluginAdminBlock(
                        "table",
                        "Tool calls",
                        Endpoint: "calls",
                        Columns:
                        [
                            new PluginAdminColumn("createdAt", "When", "time"),
                            new PluginAdminColumn("player", "Staff member"),
                            new PluginAdminColumn("tokenId", "Token", "number"),
                            new PluginAdminColumn("tool", "Tool"),
                            new PluginAdminColumn("arguments", "Arguments"),
                            new PluginAdminColumn("status", "Result", "badge"),
                            new PluginAdminColumn("durationMs", "ms", "number"),
                        ],
                        Filters: [new PluginAdminField("tool", "Tool")]
                    ),
                ],
                "Every tool an assistant called, as whom, with what."
            )
        );
    }

    /// <summary>The address assistants connect to: the plugin routes' public address, else the site's.</summary>
    public string EndpointUrl()
    {
        var root = config["Turbo:Web:PluginRoutes:PublicUrl"] is { Length: > 0 } routes
            ? routes
            : config["Turbo:Web:SiteUrl"] ?? "https://your-hotel";

        return $"{root.TrimEnd('/')}/api/plugins/mcp/mcp";
    }

    private string ConnectText() =>
        $"""
            Make a token below, then give your assistant the hotel's address and the token. It acts as you: it is offered the tools your permissions allow, read-only ones only for a read-only token, and every call is logged.

            **Address:** {EndpointUrl()}

            **Claude Code:** claude mcp add --transport http hotel {EndpointUrl()} --header "Authorization: Bearer <token>"

            **Claude Desktop, Cursor and others:** add the address as an HTTP (streamable) MCP server and sign in with your hotel account. The consent page lets you choose read-only access. Bearer tokens remain supported. OAuth connections appear in the tokens list and can be revoked there.
            """;

    private async Task<bool> ManagesAsync(int player, CancellationToken ct) =>
        await hotel.Players.HasPermissionAsync(player, McpNodes.MANAGE, ct);

    private async Task<PluginAdminResult> ListTokensAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        var all = await ManagesAsync(request.PlayerId, ct);
        var page =
            request.Query.TryGetValue("page", out var p) && int.TryParse(p, out var n) ? n : 1;
        var (items, total) = await tokens.ListAsync(
            all ? null : request.PlayerId,
            page,
            PAGE_SIZE,
            ct
        );
        var names = await hotel.Players.NamesAsync(
            [
                .. items
                    .Select(x => x.PlayerId)
                    .Distinct()
                    .Select(x => (Turbo.Primitives.Players.PlayerId)x),
            ],
            ct
        );
        var now = hotel.Clock.GetUtcNow().UtcDateTime;

        return PluginAdminResult.Ok(
            new
            {
                items = items.Select(x => new
                {
                    id = x.Id,
                    label = x.Label,
                    owner = names.TryGetValue(x.PlayerId, out var name) ? name : $"#{x.PlayerId}",
                    prefix = x.Prefix + "...",
                    readOnly = x.ReadOnly,
                    state = State(x, now),
                    expiresAt = x.ExpiresAt,
                    lastUsedAt = x.LastUsedAt,
                }),
                total,
                pageSize = PAGE_SIZE,
            }
        );
    }

    private async Task<PluginAdminResult> CreateAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        var body = request.Body;
        var label =
            body?.TryGetProperty("label", out var l) == true
                ? l.GetString() ?? string.Empty
                : string.Empty;
        var readOnly =
            body?.TryGetProperty("readOnly", out var r) == true
            && r.ValueKind == System.Text.Json.JsonValueKind.True;
        int? days =
            body?.TryGetProperty("days", out var d) == true && d.TryGetInt32(out var given)
                ? given
                : null;

        if (string.IsNullOrWhiteSpace(label))
            return PluginAdminResult.BadRequest("Give the token a name: where it is used.");

        // Always the maker's own: a token acts as the one who made it, never as anyone else.
        var (token, _) = await tokens.CreateAsync(request.PlayerId, label, readOnly, days, ct);

        return PluginAdminResult.Ok(new { id = token.Id });
    }

    private async Task<PluginAdminResult> GetTokenAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        if (
            !int.TryParse(request.Route["id"], out var id)
            || await tokens.GetAsync(id, ct) is not { } token
            || (token.PlayerId != request.PlayerId && !await ManagesAsync(request.PlayerId, ct))
        )
            return PluginAdminResult.NotFound("There is no such token.");

        var names = await hotel.Players.NamesAsync([token.PlayerId], ct);

        return PluginAdminResult.Ok(
            new
            {
                id = token.Id,
                label = token.Label,
                token = tokens.Reveal(token.Id, request.PlayerId) is { } secret
                    ? $"{secret}  (copy it now: it is shown once, for ten minutes)"
                    : $"{token.Prefix}... (shown once, when it was made)",
                owner = names.TryGetValue(token.PlayerId, out var name)
                    ? name
                    : $"#{token.PlayerId}",
                readOnly = token.ReadOnly,
                state = State(token, hotel.Clock.GetUtcNow().UtcDateTime),
                createdAt = token.CreatedAt,
                expiresAt = token.ExpiresAt,
                lastUsedAt = token.LastUsedAt,
            }
        );
    }

    private async Task<PluginAdminResult> RevokeAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        if (
            !int.TryParse(request.Route["id"], out var id)
            || await tokens.GetAsync(id, ct) is not { } token
            || (token.PlayerId != request.PlayerId && !await ManagesAsync(request.PlayerId, ct))
        )
            return PluginAdminResult.NotFound("There is no such token.");

        await tokens.RevokeAsync(id, request.PlayerId, ct);

        return PluginAdminResult.NoContent();
    }

    private async Task<PluginAdminResult> ListCallsAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        var page =
            request.Query.TryGetValue("page", out var p) && int.TryParse(p, out var n) ? n : 1;
        request.Query.TryGetValue("tool", out var tool);
        var (items, total) = await audit.ListAsync(tool, null, page, PAGE_SIZE, ct);
        var names = await hotel.Players.NamesAsync(
            [
                .. items
                    .Select(x => x.PlayerId)
                    .Distinct()
                    .Select(x => (Turbo.Primitives.Players.PlayerId)x),
            ],
            ct
        );

        return PluginAdminResult.Ok(
            new
            {
                items = items.Select(x => new
                {
                    id = x.Id,
                    createdAt = x.CreatedAt,
                    player = names.TryGetValue(x.PlayerId, out var name) ? name : $"#{x.PlayerId}",
                    tokenId = x.TokenId,
                    tool = x.Tool,
                    arguments = x.Arguments,
                    status = x.Status,
                    durationMs = x.DurationMs,
                }),
                total,
                pageSize = PAGE_SIZE,
            }
        );
    }

    private static string State(Database.McpTokenEntity token, DateTime now) =>
        token.RevokedAt is not null ? "revoked"
        : token.ExpiresAt <= now ? "expired"
        : "active";
}
