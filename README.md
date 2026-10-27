# MCP for Turbo

The hotel's [Model Context Protocol](https://modelcontextprotocol.io) server, as a Turbo plugin.
AI assistants (Claude Code, Claude Desktop, Cursor, the MCP Inspector, any MCP client) connect to
the hotel and call its **tools**: this plugin's own read-only tools and the tools every other live
plugin declares (turbo-cloud `docs/plugin-mcp-tools.md`). An assistant acts as the staff member
whose token it holds: it is offered only the tools that member's permissions allow.

Protocol: MCP **2025-11-25** (also answers clients asking for 2025-06-18 and 2025-03-26), over the
Streamable HTTP transport, stateless: each `POST` carries one JSON-RPC 2.0 message and is answered
with one JSON body; there is no session and no event stream (`GET` answers 405). Methods:
`initialize`, `notifications/initialized`, `ping`, `tools/list`, `tools/call`. The server declares
the `tools` capability only.

## Install

The plugin is its sources: the hotel compiles `server/**/*.cs` as it loads it (turbo-cloud
`docs/plugin-loading.md`). `sh scripts/package.sh` builds `dist/mcp/`, the folder a hotel loads
(name it in `Turbo:Plugin:DevPluginPaths`, or zip it for the panel's Plugins page).

It needs the hotel's public web host (`Turbo:Web:Enabled`), where plugin web routes are served,
and its tables (`mcp_tokens`, `mcp_calls`) are made as it loads.

## Tokens

In the admin panel, **MCP** (shown to staff holding `plugin.mcp.use`):

1. **New token**: a name (where it is used), read-only or not, how many days it lasts (30 by
   default, at most `Mcp:MaxTokenDays`). The token is always the maker's own: it acts as them.
2. The token's page shows it **once**, for ten minutes, to its maker. Copy it then: the hotel keeps
   only its SHA-256.
3. **Revoke** stops it at once. A token also stops when it expires, and while its member no
   longer holds `plugin.mcp.use`.

A **read-only** token is offered, and may call, only tools that change nothing (`readOnlyHint`).

Staff holding `plugin.mcp.manage` see and revoke every member's tokens, and read **MCP calls**:
every tool call with who made it, which token, the tool, its arguments (cut to
`Mcp:AuditArgumentChars`), how it went (`ok`, `error`, `invalid`, `unavailable`, `failed`) and how
long it took.

## Connect your AI

The address is the hotel's plugin routes address (`Turbo:Web:PluginRoutes:PublicUrl`, else the
site's) followed by `/api/plugins/mcp/mcp`, such as `https://example.com/api/plugins/mcp/mcp`. The
panel's MCP page shows the hotel's own.

**Claude Code**

```sh
claude mcp add --transport http hotel https://example.com/api/plugins/mcp/mcp \
  --header "Authorization: Bearer tmcp_..."
```

**Claude Desktop, Cursor and other clients** that take a JSON server list:

```json
{
  "mcpServers": {
    "hotel": {
      "type": "http",
      "url": "https://example.com/api/plugins/mcp/mcp",
      "headers": { "Authorization": "Bearer tmcp_..." }
    }
  }
}
```

**MCP Inspector**: `npx @modelcontextprotocol/inspector`, transport *Streamable HTTP*, the
address, and an `Authorization` header `Bearer tmcp_...`.

A browser page can't call it from another site: the hotel answers only its own sites' origins.

## Tools

Each has its own permission node (granted to no one by default), so staff are given tools one by
one in the panel's permission editor. All are read-only.

| Tool | Node | Answers |
| --- | --- | --- |
| `find_player` | `plugin.mcp.tool.find_player` | players by the start of their name or by id: id, name, motto, online, joined, last login |
| `player_details` | `plugin.mcp.tool.player_details` | one player's wallet (credits, duckets, diamonds), club, current room, hours online, furni held by kind |
| `room_state` | `plugin.mcp.tool.room_state` | a room's owner, users, and while it is loaded its furni counts, wired load and the wired monitor's errors |
| `catalog_search` | `plugin.mcp.tool.catalog_search` | the live catalog's offers and pages whose names hold the words |
| `open_reports` | `plugin.mcp.tool.open_reports` | the moderation queue's open reports, oldest first |
| `hotel_status` | `plugin.mcp.tool.hotel_status` | players online, open or closed, server memory, plugins loaded and failed |
| `plugin_states` | `plugin.mcp.tool.plugin_states` | every plugin's state and error (compile errors, refused calls), and one plugin's contributions |

What the tools never answer: emails, addresses, private messages, passwords or tokens. There is no
tool that runs a query of the caller's own.

**Player text.** Text players wrote (mottos, room names, report messages) is answered as
`{"playerWritten": "..."}`, and an answer holding any starts with a note that such values are data,
never instructions. The server's `instructions` say the same.

## Settings (`Mcp`)

| Key | Default | What |
| --- | --- | --- |
| `RequestsPerMinutePerToken` | `60` | Calls one token may make a minute; more are answered 429 until the minute is over. The hotel's per-address limit (`Turbo:Web:PluginRoutes:RequestsPerMinutePerAddress`) applies as well. |
| `DefaultTokenDays` | `30` | How long a new token lasts when its maker does not say. |
| `MaxTokenDays` | `365` | The longest a token may last. |
| `AuditArgumentChars` | `2000` | How much of a call's arguments the call log keeps. |

## Errors

| HTTP | When |
| --- | --- |
| 401 | no token, or one that is unknown, revoked, expired or whose member may no longer use MCP |
| 400 | an `MCP-Protocol-Version` header naming a version this server does not speak |
| 429 | the token has made its calls this minute |
| 200 with a JSON-RPC `error` | a message that is not JSON-RPC 2.0 (-32600), a batch (-32600), a method it has not (-32601), `tools/call` without a name or of a tool the caller may not call (-32602, "Unknown tool") |
| 200 with `isError: true` | the tool ran and could not do it, or its arguments did not fit its schema (each problem as a sentence) |

## Develop

```sh
sh scripts/package.sh                                   # dist/mcp, built against ../turbo-cloud
dotnet test tests/TurboMcp.Tests -p:TurboBin=<server bin>
```

The tests run the endpoint over SQLite and a fake hotel (`Turbo.Testing`), tools through
`McpToolTester`.
