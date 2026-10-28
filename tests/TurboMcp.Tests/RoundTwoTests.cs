using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Web;
using Turbo.Testing;
using TurboMcp.Tools;
using Xunit;

namespace TurboMcp.Tests;

public sealed class RoundTwoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Rpc(string method, object? parameters = null) =>
        new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = parameters,
        };

    private static PluginWebRequest Web(
        Dictionary<string, string>? query = null,
        Dictionary<string, string>? form = null,
        int? player = null,
        object? body = null
    ) =>
        new(
            "POST",
            "oauth",
            new Dictionary<string, string>(),
            query ?? [],
            body is null ? null : JsonSerializer.SerializeToElement(body),
            player,
            "127.0.0.1"
        )
        {
            Form = form ?? [],
        };

    [Fact]
    public async Task Resources_and_prompts_are_filtered_and_read_on_the_staff_account()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff", McpNodes.PLAYER_RESOURCE, McpNodes.SUMMARISE_REPORTS);
        var player = host.Hotel.Players.Add("Alice", online: true);
        var token = await host.TokenAsync(staff, readOnly: true);
        var templates = TestHost
            .Json(await host.PostAsync(token, Rpc("resources/templates/list")))
            .GetProperty("result")
            .GetProperty("resourceTemplates");
        Assert.Equal(
            "turbo://players/{playerId}",
            Assert.Single(templates.EnumerateArray()).GetProperty("uriTemplate").GetString()
        );
        var read = TestHost
            .Json(
                await host.PostAsync(
                    token,
                    Rpc("resources/read", new { uri = $"turbo://players/{player.Id.Value}" })
                )
            )
            .GetProperty("result");
        var text = read.GetProperty("contents")[0].GetProperty("text").GetString()!;
        Assert.Contains("Alice", text);
        Assert.DoesNotContain("wallet", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currentRoom", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("online", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastLogin", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "summarise_reports",
            TestHost
                .Json(await host.PostAsync(token, Rpc("prompts/list")))
                .GetProperty("result")
                .GetProperty("prompts")[0]
                .GetProperty("name")
                .GetString()
        );
        Assert.Contains(
            "open_reports",
            TestHost
                .Json(
                    await host.PostAsync(
                        token,
                        Rpc("prompts/get", new { name = "summarise_reports" })
                    )
                )
                .GetProperty("result")
                .GetProperty("messages")[0]
                .GetProperty("content")
                .GetProperty("text")
                .GetString()
        );
        staff.Permissions.Remove(McpNodes.PLAYER_RESOURCE);
        Assert.Equal(
            -32002,
            TestHost
                .Json(
                    await host.PostAsync(
                        token,
                        Rpc("resources/read", new { uri = $"turbo://players/{player.Id.Value}" })
                    )
                )
                .GetProperty("error")
                .GetProperty("code")
                .GetInt32()
        );
    }

    [Fact]
    public async Task Catalog_search_returns_only_currently_callable_tools_and_direct_calls_still_work()
    {
        await using var host = new TestHost(new McpSettings { ToolCatalogThreshold = 0 });
        var staff = host.Staff("Staff", McpNodes.FIND_PLAYER, "test.warn");
        var token = await host.TokenAsync(staff, readOnly: true);
        Assert.Equal(
            "search_tools",
            TestHost
                .Json(await host.PostAsync(token, Rpc("tools/list")))
                .GetProperty("result")
                .GetProperty("tools")[0]
                .GetProperty("name")
                .GetString()
        );
        var found = TestHost
            .Json(
                await host.PostAsync(
                    token,
                    Rpc("tools/call", new { name = "search_tools", arguments = new { query = "" } })
                )
            )
            .GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("tools");
        Assert.Equal(
            "find_player",
            Assert.Single(found.EnumerateArray()).GetProperty("name").GetString()
        );
        Assert.True(
            TestHost
                .Json(
                    await host.PostAsync(
                        token,
                        Rpc(
                            "tools/call",
                            new { name = "find_player", arguments = new { name = "Staff" } }
                        )
                    )
                )
                .TryGetProperty("result", out _)
        );
        var bySchema = TestHost
            .Json(
                await host.PostAsync(
                    token,
                    Rpc(
                        "tools/call",
                        new { name = "search_tools", arguments = new { query = "lastLoginAt" } }
                    )
                )
            )
            .GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("tools");
        Assert.Equal(
            "find_player",
            Assert.Single(bySchema.EnumerateArray()).GetProperty("name").GetString()
        );
        staff.Permissions.Remove(McpNodes.FIND_PLAYER);
        Assert.Equal(
            -32602,
            TestHost
                .Json(
                    await host.PostAsync(
                        token,
                        Rpc(
                            "tools/call",
                            new { name = "find_player", arguments = new { name = "Staff" } }
                        )
                    )
                )
                .GetProperty("error")
                .GetProperty("code")
                .GetInt32()
        );
    }

    [Fact]
    public async Task Write_tools_wait_for_approval_and_read_only_access_never_offers_them()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff", McpNodes.ALERT_PLAYER, McpNodes.KICK_FROM_ROOM);
        var target = host.Hotel.Players.Add("Alice", online: true);
        var room = host.Hotel.Rooms.Add("Room", staff);
        host.Hotel.Rooms.Enter(room, target);
        var token = await host.TokenAsync(staff);
        foreach (
            var call in new[]
            {
                Rpc(
                    "tools/call",
                    new
                    {
                        name = "alert_player",
                        arguments = (object)new { playerId = target.Id.Value, message = "Hello" },
                    }
                ),
                Rpc(
                    "tools/call",
                    new
                    {
                        name = "kick_from_room",
                        arguments = (object)
                            new { playerId = target.Id.Value, roomId = room.Id.Value },
                    }
                ),
            }
        )
        {
            var result = TestHost.Json(await host.PostAsync(token, call)).GetProperty("result");
            Assert.False(result.GetProperty("isError").GetBoolean());
            Assert.Equal(
                "pending",
                result.GetProperty("structuredContent").GetProperty("state").GetString()
            );
        }
        host.Hotel.Notify.AssertNotAlerted(target);
        Assert.Contains(target.Id, await host.Hotel.Rooms.PlayersAsync(room, Ct));
        var readOnly = await host.TokenAsync(staff, readOnly: true);
        Assert.Empty(
            TestHost
                .Json(await host.PostAsync(readOnly, Rpc("tools/list")))
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
        );
        var tester = new McpToolTester(
            new AlertPlayerTool(host.Hotel),
            new KickFromRoomTool(host.Hotel)
        ).As(staff);
        (
            await tester.CallAsync(
                "alert_player",
                new { playerId = target.Id.Value, message = "Hello" },
                Ct
            )
        ).AssertOk();
        Assert.Equal("Hello", host.Hotel.Notify.AssertAlerted(target, "mcp.staff.alert").Text);
        (
            await tester.CallAsync(
                "kick_from_room",
                new { playerId = target.Id.Value, roomId = room.Id.Value },
                Ct
            )
        ).AssertOk();
        Assert.DoesNotContain(target.Id, await host.Hotel.Rooms.PlayersAsync(room, Ct));
    }

    private static string Register(TestHost host) =>
        TestHost
            .Json(
                host.OAuth.Register(
                    Web(
                        body: new
                        {
                            client_name = "Test Assistant",
                            redirect_uris = new[] { "http://127.0.0.1:5511/callback" },
                        }
                    )
                )
            )
            .GetProperty("client_id")
            .GetString()!;

    private static Dictionary<string, string> Query(
        TestHost host,
        string client,
        string verifier
    ) =>
        new()
        {
            ["client_id"] = client,
            ["redirect_uri"] = "http://127.0.0.1:5511/callback",
            ["resource"] = host.OAuth.Resource,
            ["response_type"] = "code",
            ["code_challenge_method"] = "S256",
            ["state"] = "opaque state",
            ["code_challenge"] = Base64Url.EncodeToString(
                SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
            ),
        };

    private static string Ticket(PluginWebResult page) =>
        Regex.Match(page.Page!, "name=\"ticket\" value=\"([^\"]+)\"").Groups[1].Value;

    private static async Task<string> Authorize(
        TestHost host,
        string client,
        string verifier,
        int player,
        bool readOnly = true
    )
    {
        var page = await host.OAuth.AuthorizeAsync(
            Web(query: Query(host, client, verifier), player: player),
            Ct
        );
        var form = new Dictionary<string, string>
        {
            ["ticket"] = Ticket(page),
            ["decision"] = "allow",
        };
        if (readOnly)
            form["readOnly"] = "true";
        var consent = await host.OAuth.ConsentAsync(Web(form: form, player: player), Ct);
        var location = consent.Headers["Location"];
        Assert.Contains("state=opaque%20state", location);
        return location.Split("code=")[1].Split('&')[0];
    }

    private static Dictionary<string, string> Exchange(
        TestHost host,
        string client,
        string verifier,
        string code
    ) =>
        new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = client,
            ["redirect_uri"] = "http://127.0.0.1:5511/callback",
            ["resource"] = host.OAuth.Resource,
            ["code_verifier"] = verifier,
            ["code"] = code,
        };

    [Fact]
    public async Task OAuth_tokens_have_staff_permissions_read_only_expiry_and_revoke_semantics()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff", McpNodes.FIND_PLAYER, "test.warn");
        var client = Register(host);
        var verifier = new string('v', 43);
        var code = await Authorize(host, client, verifier, staff.Id);
        var form = Exchange(host, client, verifier, code);
        var exchanged = await host.OAuth.TokenAsync(Web(form: form), Ct);
        Assert.Equal(200, exchanged.Status);
        Assert.Equal("no-store", exchanged.Headers["Cache-Control"]);
        var token = TestHost.Json(exchanged).GetProperty("access_token").GetString()!;
        var grant = await host.Tokens.FindAsync("Bearer " + token, Ct);
        Assert.NotNull(grant);
        Assert.True(grant.ReadOnly);
        Assert.Null(host.Tokens.Reveal(grant.TokenId, staff.Id));
        Assert.Equal(400, (await host.OAuth.TokenAsync(Web(form: form), Ct)).Status);
        Assert.Equal(
            "find_player",
            Assert
                .Single(
                    TestHost
                        .Json(await host.PostAsync(token, Rpc("tools/list")))
                        .GetProperty("result")
                        .GetProperty("tools")
                        .EnumerateArray()
                )
                .GetProperty("name")
                .GetString()
        );
        await host.Tokens.RevokeAsync(grant.TokenId, staff.Id, Ct);
        Assert.Equal(401, (await host.PostAsync(token, Rpc("ping"))).Status);
        var another = await Authorize(host, client, verifier, staff.Id, readOnly: false);
        var full = TestHost
            .Json(
                await host.OAuth.TokenAsync(
                    Web(form: Exchange(host, client, verifier, another)),
                    Ct
                )
            )
            .GetProperty("access_token")
            .GetString()!;
        host.Hotel.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(401, (await host.PostAsync(full, Rpc("ping"))).Status);
    }

    [Fact]
    public async Task OAuth_rejects_redirect_substitution_pkce_replay_wrong_account_and_expired_consent()
    {
        await using var host = new TestHost();
        var staff = host.Staff("Staff");
        var other = host.Staff("Other");
        var client = Register(host);
        var verifier = new string('v', 43);
        var query = Query(host, client, verifier);
        query["redirect_uri"] = "https://attacker.test";
        Assert.Equal(
            400,
            (await host.OAuth.AuthorizeAsync(Web(query: query, player: staff.Id), Ct)).Status
        );
        query = Query(host, client, verifier);
        query["code_challenge_method"] = "plain";
        Assert.Equal(
            400,
            (await host.OAuth.AuthorizeAsync(Web(query: query, player: staff.Id), Ct)).Status
        );
        var page = await host.OAuth.AuthorizeAsync(
            Web(query: Query(host, client, verifier), player: staff.Id),
            Ct
        );
        var consent = new Dictionary<string, string>
        {
            ["ticket"] = Ticket(page),
            ["decision"] = "allow",
        };
        Assert.Equal(
            400,
            (await host.OAuth.ConsentAsync(Web(form: consent, player: other.Id), Ct)).Status
        );
        host.Hotel.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(
            400,
            (await host.OAuth.ConsentAsync(Web(form: consent, player: staff.Id), Ct)).Status
        );
        var code = await Authorize(host, client, verifier, staff.Id);
        Assert.Equal(
            400,
            (
                await host.OAuth.TokenAsync(
                    Web(form: Exchange(host, client, new string('x', 43), code)),
                    Ct
                )
            ).Status
        );
        Assert.Equal(
            400,
            (
                await host.OAuth.TokenAsync(Web(form: Exchange(host, client, verifier, code)), Ct)
            ).Status
        );
    }

    [Fact]
    public async Task Unused_registration_capacity_recovers_while_a_consented_client_remains_usable()
    {
        await using var host = new TestHost(new McpSettings { OAuthMaxPending = 2 });
        var staff = host.Staff("Staff");
        var legitimate = Register(host);
        var verifier = new string('v', 43);
        await Authorize(host, legitimate, verifier, staff.Id);
        var unused = Register(host);
        var registration = Web(
            body: new { redirect_uris = new[] { "http://127.0.0.1:5511/callback" } }
        );
        Assert.Equal(503, host.OAuth.Register(registration).Status);

        host.Hotel.Clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(201, host.OAuth.Register(registration).Status);
        Assert.Equal(
            400,
            (
                await host.OAuth.AuthorizeAsync(
                    Web(query: Query(host, unused, verifier), player: staff.Id),
                    Ct
                )
            ).Status
        );
        var code = await Authorize(host, legitimate, verifier, staff.Id);
        Assert.Equal(
            200,
            (
                await host.OAuth.TokenAsync(
                    Web(form: Exchange(host, legitimate, verifier, code)),
                    Ct
                )
            ).Status
        );
    }

    [Fact]
    public async Task Discovery_and_registration_are_explicit_and_bearer_tokens_stay_supported()
    {
        await using var host = new TestHost(new McpSettings { OAuthDynamicRegistration = false });
        Assert.False(
            TestHost.Json(host.OAuth.Metadata(false)).TryGetProperty("registration_endpoint", out _)
        );
        Assert.Equal(404, host.OAuth.Register(Web()).Status);
        Assert.Equal(
            host.OAuth.Resource,
            TestHost.Json(host.OAuth.Metadata(true)).GetProperty("resource").GetString()
        );
        var unauthorized = await host.PostAsync(null, Rpc("ping"));
        Assert.Contains("resource_metadata=", unauthorized.Headers["WWW-Authenticate"]);
        var token = await host.TokenAsync(host.Staff("Staff"));
        Assert.Equal(200, (await host.PostAsync(token, Rpc("ping"))).Status);
    }
}
