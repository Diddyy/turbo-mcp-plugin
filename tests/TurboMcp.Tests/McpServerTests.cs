using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Testing;
using TurboMcp.Tools;
using Xunit;

namespace TurboMcp.Tests;

/// <summary>
/// The MCP endpoint end to end: JSON-RPC 2.0 messages in, answers as the MCP specification
/// (2025-11-25) shapes them out; tokens that sign a call in and those that don't; what a token
/// is offered; the call log and the rate limit.
/// </summary>
public sealed class McpServerTests
{
    private static object Request(int id, string method, object? parameters = null) =>
        parameters is null
            ? new
            {
                jsonrpc = "2.0",
                id,
                method,
            }
            : new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            };

    [Fact]
    public async Task Initialize_answers_the_version_asked_for_when_it_speaks_it_else_its_newest()
    {
        await using var host = new TestHost();
        var token = await host.TokenAsync(host.Staff("Staff"));

        var asked = TestHost.Json(
            await host.PostAsync(
                token,
                Request(
                    1,
                    "initialize",
                    new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { },
                        clientInfo = new { name = "test", version = "1" },
                    }
                )
            )
        );
        var unknown = TestHost.Json(
            await host.PostAsync(
                token,
                Request(2, "initialize", new { protocolVersion = "2024-01-01" })
            )
        );

        Assert.Equal("2.0", asked.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, asked.GetProperty("id").GetInt32());
        Assert.Equal(
            "2025-06-18",
            asked.GetProperty("result").GetProperty("protocolVersion").GetString()
        );
        Assert.False(
            asked
                .GetProperty("result")
                .GetProperty("capabilities")
                .GetProperty("tools")
                .GetProperty("listChanged")
                .GetBoolean()
        );
        Assert.Equal(
            "turbo-mcp",
            asked.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()
        );
        Assert.Equal(
            "2025-11-25",
            unknown.GetProperty("result").GetProperty("protocolVersion").GetString()
        );
    }

    [Fact]
    public async Task A_notification_is_taken_with_202_and_no_body_and_ping_answers_empty()
    {
        await using var host = new TestHost();
        var token = await host.TokenAsync(host.Staff("Staff"));

        var initialized = await host.PostAsync(
            token,
            new { jsonrpc = "2.0", method = "notifications/initialized" }
        );
        var ping = TestHost.Json(await host.PostAsync(token, Request(3, "ping")));

        Assert.Equal(202, initialized.Status);
        Assert.Null(initialized.Value);
        Assert.Equal("{}", ping.GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task Only_a_live_token_of_a_member_who_may_use_mcp_signs_a_call_in()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff");
        var live = await host.TokenAsync(staff);
        var revoked = await host.TokenAsync(staff);
        var expiring = await host.TokenAsync(staff, days: 1);
        var demoted = host.Staff("Demoted");
        var demotedToken = await host.TokenAsync(demoted);
        var list = Request(1, "tools/list");

        // Tokens count from 1: the second one made is revoked.
        await host.Tokens.RevokeAsync(2, staff.Id, default);
        demoted.Permissions.Remove(McpNodes.USE);

        Assert.Equal(401, (await host.PostAsync(null, list)).Status);
        Assert.Equal(401, (await host.PostAsync("tmcp_not-a-token", list)).Status);
        Assert.Equal(401, (await host.PostAsync(revoked, list)).Status);
        Assert.Equal(401, (await host.PostAsync(demotedToken, list)).Status);
        Assert.Equal(200, (await host.PostAsync(live, list)).Status);
        Assert.Equal(200, (await host.PostAsync(expiring, list)).Status);

        host.Hotel.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(401, (await host.PostAsync(expiring, list)).Status);
        Assert.Equal(200, (await host.PostAsync(live, list)).Status);
    }

    [Fact]
    public async Task A_token_is_kept_as_its_hash_and_shown_only_to_its_maker_for_ten_minutes()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff");
        var (token, secret) = await host.Tokens.CreateAsync(staff, "laptop", false, null, default);
        var kept = await host.Tokens.GetAsync(token.Id, default);

        Assert.StartsWith("tmcp_", secret);
        Assert.Equal(McpTokens.Hash(secret), kept!.Hash);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(kept));
        Assert.Equal(host.Hotel.Clock.GetUtcNow().UtcDateTime.AddDays(30), kept.ExpiresAt);
        Assert.Equal(secret, host.Tokens.Reveal(token.Id, staff.Id));
        Assert.Null(host.Tokens.Reveal(token.Id, staff.Id + 1));

        host.Hotel.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Null(host.Tokens.Reveal(token.Id, staff.Id));
    }

    [Fact]
    public async Task Tools_list_offers_the_tools_the_member_holds_and_a_read_only_token_only_read_only_ones()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff", McpNodes.FIND_PLAYER, "test.warn");
        var helper = host.Staff("Helper", McpNodes.FIND_PLAYER);

        var full = Names(
            await host.PostAsync(await host.TokenAsync(staff), Request(1, "tools/list"))
        );
        var readOnly = Names(
            await host.PostAsync(
                await host.TokenAsync(staff, readOnly: true),
                Request(1, "tools/list")
            )
        );
        var limited = Names(
            await host.PostAsync(await host.TokenAsync(helper), Request(1, "tools/list"))
        );

        Assert.Equal(["find_player", "warn_player"], full);
        Assert.Equal(["find_player"], readOnly);
        Assert.Equal(["find_player"], limited);

        var tool = TestHost
            .Json(await host.PostAsync(await host.TokenAsync(staff), Request(1, "tools/list")))
            .GetProperty("result")
            .GetProperty("tools")[0];

        Assert.Equal("Find a player", tool.GetProperty("title").GetString());
        Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.Equal("object", tool.GetProperty("outputSchema").GetProperty("type").GetString());
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.False(tool.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());
    }

    [Fact]
    public async Task A_read_only_token_cannot_call_a_tool_that_changes_things_nor_a_member_a_tool_they_lack()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff", McpNodes.FIND_PLAYER, "test.warn");
        var helper = host.Staff("Helper", McpNodes.FIND_PLAYER);
        var warn = Request(
            1,
            "tools/call",
            new { name = "warn_player", arguments = new { id = 1 } }
        );

        var readOnly = TestHost.Json(
            await host.PostAsync(await host.TokenAsync(staff, readOnly: true), warn)
        );
        var lacking = TestHost.Json(await host.PostAsync(await host.TokenAsync(helper), warn));
        var allowed = TestHost.Json(await host.PostAsync(await host.TokenAsync(staff), warn));

        Assert.Equal(-32602, readOnly.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(
            "Unknown tool: warn_player. tools/list names the tools you may call.",
            readOnly.GetProperty("error").GetProperty("message").GetString()
        );
        Assert.Equal(
            readOnly.GetProperty("error").GetRawText(),
            lacking.GetProperty("error").GetRawText()
        );
        Assert.Equal(
            "Warned 1.",
            allowed.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()
        );
    }

    [Fact]
    public async Task Tools_call_answers_text_and_structured_content_and_is_logged()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff", McpNodes.FIND_PLAYER);
        var alice = host.Hotel.Players.Add("Alice", online: true);
        var token = await host.TokenAsync(staff);

        var found = TestHost
            .Json(
                await host.PostAsync(
                    token,
                    Request(
                        7,
                        "tools/call",
                        new { name = "find_player", arguments = new { name = "ali" } }
                    )
                )
            )
            .GetProperty("result");
        var wrong = TestHost
            .Json(
                await host.PostAsync(
                    token,
                    Request(
                        8,
                        "tools/call",
                        new { name = "find_player", arguments = new { name = "ali", take = 500 } }
                    )
                )
            )
            .GetProperty("result");

        Assert.False(found.GetProperty("isError").GetBoolean());
        var player = found.GetProperty("structuredContent").GetProperty("players")[0];
        Assert.Equal(alice.Id.Value, player.GetProperty("id").GetInt32());
        Assert.True(player.GetProperty("online").GetBoolean());
        Assert.Equal("", player.GetProperty("motto").GetProperty("playerWritten").GetString());
        Assert.Equal(
            found.GetProperty("structuredContent").GetRawText(),
            found
                .GetProperty("content")[found.GetProperty("content").GetArrayLength() - 1]
                .GetProperty("text")
                .GetString()
        );
        Assert.True(wrong.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "The argument \"take\" must be at most 50; it was 500.",
            wrong.GetProperty("content")[0].GetProperty("text").GetString()
        );

        var (calls, total) = await host.Audit.ListAsync(null, null, 1, 10, default);

        Assert.Equal(2, total);
        Assert.Equal(["invalid", "ok"], calls.Select(x => x.Status));
        Assert.All(calls, x => Assert.Equal(("find_player", staff.Id.Value), (x.Tool, x.PlayerId)));
        Assert.Equal("""{"name":"ali"}""", calls[1].Arguments);
    }

    [Fact]
    public async Task A_token_past_its_calls_this_minute_is_answered_429_until_the_next()
    {
        await using var host = new TestHost(new McpSettings { RequestsPerMinutePerToken = 2 });
        var token = await host.TokenAsync(host.Staff("Staff"));
        var other = await host.TokenAsync(host.Staff("Other"));

        Assert.Equal(200, (await host.PostAsync(token, Request(1, "ping"))).Status);
        Assert.Equal(200, (await host.PostAsync(token, Request(2, "ping"))).Status);
        Assert.Equal(429, (await host.PostAsync(token, Request(3, "ping"))).Status);
        Assert.Equal(200, (await host.PostAsync(other, Request(1, "ping"))).Status);

        host.Hotel.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(200, (await host.PostAsync(token, Request(4, "ping"))).Status);
    }

    [Fact]
    public async Task Messages_it_cannot_take_are_answered_as_json_rpc_errors()
    {
        await using var host = new TestHost();
        var token = await host.TokenAsync(host.Staff("Staff"));

        Assert.Equal(-32600, Code(await host.PostAsync(token, "[]")));
        Assert.Equal(-32600, Code(await host.PostAsync(token, new { id = 1, method = "ping" })));
        Assert.Equal(-32601, Code(await host.PostAsync(token, Request(1, "unknown/method"))));
        Assert.Equal(
            -32602,
            Code(await host.PostAsync(token, Request(1, "tools/call", new { arguments = new { } })))
        );
        Assert.Equal(
            400,
            (
                await host.PostAsync(
                    token,
                    Request(1, "ping"),
                    new() { ["mcp-protocol-version"] = "2099-01-01" }
                )
            ).Status
        );
        Assert.Equal(
            200,
            (
                await host.PostAsync(
                    token,
                    Request(1, "ping"),
                    new() { ["mcp-protocol-version"] = "2025-11-25" }
                )
            ).Status
        );
    }

    [Fact]
    public async Task Find_player_marks_a_motto_as_player_text()
    {
        var hotel = new FakeHotel();
        var staff = hotel.Players.Add("Staff");
        hotel.Players.Add("Mallory");
        staff.Permissions.Add(McpNodes.FIND_PLAYER);

        var call = (
            await new McpToolTester(new FindPlayerTool(hotel))
                .As(staff)
                .CallAsync("find_player", new { name = "mal" })
        ).AssertOk();

        Assert.Equal("Mallory", call.Data<FindPlayerOutput>().Players.Single().Name);
        Assert.StartsWith("Values under \"playerWritten\"", call.Result.Text[0]);
        (
            await new McpToolTester(new FindPlayerTool(hotel))
                .As(staff)
                .CallAsync("find_player", new { })
        ).AssertError("Give a name (or the start of one) or an id.");
    }

    private static List<string> Names(Turbo.Contracts.Plugins.Web.PluginWebResult result) =>
        [
            .. TestHost
                .Json(result)
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(x => x.GetProperty("name").GetString()!),
        ];

    private static int Code(Turbo.Contracts.Plugins.Web.PluginWebResult result) =>
        TestHost.Json(result).GetProperty("error").GetProperty("code").GetInt32();
}
