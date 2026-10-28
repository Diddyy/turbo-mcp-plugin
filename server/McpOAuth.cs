using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Web;

namespace TurboMcp;

/// <summary>Public-client authorization with S256 PKCE; grants become the same revocable staff tokens as panel tokens.</summary>
public sealed class McpOAuth(
    IHotel hotel,
    McpTokens tokens,
    IConfiguration config,
    IOptionsMonitor<McpSettings> settings
)
{
    private sealed record Client(string Name, string[] Redirects, DateTimeOffset Until);

    private sealed record Authorization(
        string Client,
        string Redirect,
        string Challenge,
        string State,
        int Player,
        DateTimeOffset Until
    );

    private sealed record Code(
        Authorization Request,
        int Player,
        bool ReadOnly,
        DateTimeOffset Until
    );

    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly ConcurrentDictionary<string, Authorization> _pending = new();
    private readonly ConcurrentDictionary<string, Code> _codes = new();
    private readonly object _gate = new();
    private DateTimeOffset Now => hotel.Clock.GetUtcNow();
    public string Issuer =>
        (
            config["Turbo:Web:PluginRoutes:PublicUrl"] is { Length: > 0 } root
                ? root
                : config["Turbo:Web:SiteUrl"] ?? ""
        ).TrimEnd('/') + "/api/plugins/mcp";
    public string Resource => Issuer + "/mcp";
    public string? ResourceMetadata =>
        Configured ? WellKnown("oauth-protected-resource", "/mcp") : null;

    private string WellKnown(string name, string suffix = "")
    {
        var uri = new Uri(Issuer);
        return uri.GetLeftPart(UriPartial.Authority)
            + "/.well-known/"
            + name
            + uri.AbsolutePath
            + suffix;
    }

    private bool Configured =>
        Uri.TryCreate(Issuer, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback);

    private static string Secret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string Get(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var value) ? value : "";

    private static bool RedirectAllowed(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback);

    private static PluginWebResult Error(string code, string description, int status = 400) =>
        new(status, new { error = code, error_description = description });

    private static PluginWebResult NoStore(PluginWebResult result) =>
        result with
        {
            Headers = new Dictionary<string, string>(result.Headers)
            {
                ["Cache-Control"] = "no-store",
                ["Pragma"] = "no-cache",
            },
        };

    private void Prune()
    {
        foreach (var item in _clients.Where(x => x.Value.Until <= Now))
            _clients.TryRemove(item.Key, out _);
        foreach (var item in _pending.Where(x => x.Value.Until <= Now))
            _pending.TryRemove(item.Key, out _);
        foreach (var item in _codes.Where(x => x.Value.Until <= Now))
            _codes.TryRemove(item.Key, out _);
    }

    public PluginWebResult Metadata(bool resource)
    {
        if (!Configured)
            return Error(
                "temporarily_unavailable",
                "Configure the hotel's public HTTPS address.",
                503
            );
        if (resource)
            return PluginWebResult.Ok(
                new
                {
                    resource = Resource,
                    authorization_servers = new[] { Issuer },
                    scopes_supported = new[] { "mcp" },
                    bearer_methods_supported = new[] { "header" },
                }
            );
        var metadata = new Dictionary<string, object>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = Issuer + "/oauth/authorize",
            ["token_endpoint"] = Issuer + "/oauth/token",
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["scopes_supported"] = new[] { "mcp" },
        };
        if (settings.CurrentValue.OAuthDynamicRegistration)
            metadata["registration_endpoint"] = Issuer + "/oauth/register";
        return PluginWebResult.Ok(metadata);
    }

    public PluginWebResult Register(PluginWebRequest request)
    {
        if (!Configured || !settings.CurrentValue.OAuthDynamicRegistration)
            return PluginWebResult.NotFound("Dynamic registration is disabled.");
        var body = request.Body;
        if (
            body is not { ValueKind: System.Text.Json.JsonValueKind.Object } json
            || !json.TryGetProperty("redirect_uris", out var uris)
            || uris.ValueKind != System.Text.Json.JsonValueKind.Array
            || uris.GetArrayLength() is < 1 or > 10
        )
            return Error("invalid_client_metadata", "Provide one to ten redirect_uris.");
        var redirects = new List<string>();
        foreach (var uri in uris.EnumerateArray())
        {
            if (
                uri.ValueKind != System.Text.Json.JsonValueKind.String
                || !RedirectAllowed(uri.GetString()!)
            )
                return Error(
                    "invalid_redirect_uri",
                    "Redirects must be HTTPS or loopback HTTP, with no fragment or user information."
                );
            redirects.Add(uri.GetString()!);
        }
        if (
            json.TryGetProperty("token_endpoint_auth_method", out var auth)
            && (
                auth.ValueKind != System.Text.Json.JsonValueKind.String
                || auth.GetString() != "none"
            )
        )
            return Error("invalid_client_metadata", "Only public clients (none) are supported.");
        var name =
            json.TryGetProperty("client_name", out var n)
            && n.ValueKind == System.Text.Json.JsonValueKind.String
                ? n.GetString()!
                : "Assistant";
        if (name.Length > 100)
            return Error("invalid_client_metadata", "client_name is at most 100 characters.");
        lock (_gate)
        {
            Prune();
            if (_clients.Count >= Math.Max(1, settings.CurrentValue.OAuthMaxPending))
                return Error(
                    "temporarily_unavailable",
                    "Registration capacity reached; try later.",
                    503
                );
            var id = Secret();
            _clients[id] = new Client(
                name,
                [.. redirects],
                Now.AddMinutes(Math.Max(1, settings.CurrentValue.OAuthRegistrationMinutes))
            );
            return NoStore(
                new PluginWebResult(
                    201,
                    new
                    {
                        client_id = id,
                        client_name = name,
                        redirect_uris = redirects,
                        token_endpoint_auth_method = "none",
                        grant_types = new[] { "authorization_code" },
                        response_types = new[] { "code" },
                    }
                )
            );
        }
    }

    private Client? FindClient(string id)
    {
        if (
            settings.CurrentValue.OAuthClients.TryGetValue(id, out var client)
            && client.RedirectUris.Length > 0
            && client.RedirectUris.All(RedirectAllowed)
        )
            return new Client(client.Name, client.RedirectUris, DateTimeOffset.MaxValue);
        return _clients.TryGetValue(id, out var found) && found.Until > Now ? found : null;
    }

    public async Task<PluginWebResult> AuthorizeAsync(
        PluginWebRequest request,
        CancellationToken ct
    )
    {
        if (!Configured)
            return Error(
                "temporarily_unavailable",
                "Configure the hotel's public HTTPS address.",
                503
            );
        var args = request.Query;
        var id = Get(args, "client_id");
        var redirect = Get(args, "redirect_uri");
        var client = FindClient(id);
        if (client is null || !client.Redirects.Contains(redirect, StringComparer.Ordinal))
            return Error("invalid_request", "Unknown client or unregistered redirect_uri.");
        var challenge = Get(args, "code_challenge");
        if (
            Get(args, "response_type") != "code"
            || Get(args, "resource") != Resource
            || Get(args, "code_challenge_method") != "S256"
            || challenge.Length != 43
            || challenge.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_')
            || Get(args, "state").Length > 2000
            || Get(args, "scope") is not ("" or "mcp")
        )
            return Error(
                "invalid_request",
                "Use response_type=code, this MCP resource, scope=mcp and PKCE S256."
            );
        if (request.PlayerId is not { } player)
        {
            var site = config["Turbo:Web:SiteUrl"] ?? "/";
            return NoStore(
                PluginWebResult.Html(
                    $"<h1>Sign in to your hotel</h1><p><a href=\"{WebUtility.HtmlEncode(site)}\">Open the hotel and sign in</a>, then return to this page and reload it to approve this connection.</p>"
                )
            );
        }
        if (!await hotel.Players.HasPermissionAsync(player, McpNodes.USE, ct))
            return PluginWebResult.Forbidden("Your account needs the MCP use permission.");
        string ticket;
        lock (_gate)
        {
            Prune();
            if (_pending.Count >= Math.Max(1, settings.CurrentValue.OAuthMaxPending))
                return Error(
                    "temporarily_unavailable",
                    "Authorization capacity reached; try later.",
                    503
                );
            ticket = Secret();
            _pending[ticket] = new Authorization(
                id,
                redirect,
                challenge,
                Get(args, "state"),
                player,
                Now.AddMinutes(Math.Max(1, settings.CurrentValue.OAuthAuthorizationMinutes))
            );
        }
        return NoStore(
            PluginWebResult.Html(
                $"<h1>Connect {WebUtility.HtmlEncode(client.Name)}?</h1><p>The assistant acts as your hotel account and can use only your current permissions. Revoke the connection on the MCP page in the admin panel.</p><form method=\"post\" action=\"{WebUtility.HtmlEncode(Issuer)}/oauth/consent\"><input type=\"hidden\" name=\"ticket\" value=\"{ticket}\"><label><input type=\"checkbox\" name=\"readOnly\" value=\"true\" checked> Read-only access</label><p><button name=\"decision\" value=\"allow\">Allow</button> <button name=\"decision\" value=\"deny\">Deny</button></p></form>"
            )
        );
    }

    public async Task<PluginWebResult> ConsentAsync(PluginWebRequest request, CancellationToken ct)
    {
        var ticket = Get(request.Form, "ticket");
        if (
            request.PlayerId is not { } player
            || !_pending.TryGetValue(ticket, out var pending)
            || pending.Player != player
        )
            return Error("invalid_request", "Consent must come from the account that opened it.");
        if (!await hotel.Players.HasPermissionAsync(player, McpNodes.USE, ct))
            return PluginWebResult.Forbidden("Your account needs the MCP use permission.");
        if (!_pending.TryRemove(ticket, out var auth) || auth.Until <= Now)
            return Error("invalid_request", "Consent expired or was already used; connect again.");
        var suffix = auth.Redirect.Contains('?') ? "&" : "?";
        var state = "&state=" + Uri.EscapeDataString(auth.State);
        if (Get(request.Form, "decision") != "allow")
            return NoStore(
                PluginWebResult.Redirect(auth.Redirect + suffix + "error=access_denied" + state)
            );
        string code;
        lock (_gate)
        {
            Prune();
            if (_codes.Count >= Math.Max(1, settings.CurrentValue.OAuthMaxPending))
                return Error(
                    "temporarily_unavailable",
                    "Authorization capacity reached; try later.",
                    503
                );
            if (FindClient(auth.Client) is null)
                return Error(
                    "invalid_request",
                    "Client registration expired; register and connect again."
                );
            if (_clients.TryGetValue(auth.Client, out var registered))
                _clients[auth.Client] = registered with
                {
                    Until = Now.AddDays(Math.Max(1, settings.CurrentValue.MaxTokenDays)),
                };
            code = Secret();
            _codes[code] = new Code(
                auth,
                player,
                Get(request.Form, "readOnly") == "true",
                Now.AddMinutes(Math.Max(1, settings.CurrentValue.OAuthCodeMinutes))
            );
        }
        return NoStore(
            PluginWebResult.Redirect(
                auth.Redirect + suffix + "code=" + Uri.EscapeDataString(code) + state
            )
        );
    }

    public async Task<PluginWebResult> TokenAsync(PluginWebRequest request, CancellationToken ct)
    {
        var args = request.Form;
        if (Get(args, "grant_type") != "authorization_code")
            return NoStore(
                Error("unsupported_grant_type", "Only authorization_code is supported.")
            );
        if (Get(args, "resource") != Resource)
            return NoStore(Error("invalid_target", "Use this MCP server's resource."));
        var verifier = Get(args, "code_verifier");
        if (
            verifier.Length is < 43 or > 128
            || verifier.Any(x =>
                !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '.' and not '_' and not '~'
            )
        )
            return NoStore(Error("invalid_grant", "A valid PKCE verifier is required."));
        var challenge = Base64Url.EncodeToString(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
        );
        var key = Get(args, "code");
        if (
            !_codes.TryRemove(key, out var code)
            || code.Until <= Now
            || code.Request.Client != Get(args, "client_id")
            || code.Request.Redirect != Get(args, "redirect_uri")
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(challenge),
                Encoding.ASCII.GetBytes(code.Request.Challenge)
            )
            || FindClient(code.Request.Client) is null
            || !await hotel.Players.HasPermissionAsync(code.Player, McpNodes.USE, ct)
        )
            return NoStore(
                Error(
                    "invalid_grant",
                    "The authorization code, client, redirect or verifier is invalid or expired."
                )
            );
        var client = FindClient(code.Request.Client)!;
        var (token, secret) = await tokens.CreateAsync(
            code.Player,
            "OAuth: " + client.Name,
            code.ReadOnly,
            null,
            ct
        );
        tokens.Hide(token.Id);
        return NoStore(
            PluginWebResult.Ok(
                new
                {
                    access_token = secret,
                    token_type = "Bearer",
                    expires_in = Math.Max(
                        0,
                        (long)(token.ExpiresAt - Now.UtcDateTime).TotalSeconds
                    ),
                    scope = "mcp",
                }
            )
        );
    }
}
