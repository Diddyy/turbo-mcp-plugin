using System.Collections.Generic;
using Turbo.Contracts.Plugins;

namespace TurboMcp;

/// <summary>The MCP server's limits.</summary>
[PluginSettings("Mcp")]
public sealed class McpSettings
{
    /// <summary>Above this many available tools, advertise search_tools instead of every tool definition.</summary>
    public int ToolCatalogThreshold { get; set; } = 40;

    /// <summary>Maximum tool definitions returned by a catalog search (clamped to 1�200).</summary>
    public int ToolSearchLimit { get; set; } = 40;

    /// <summary>Allow public OAuth clients to register redirect addresses.</summary>
    public bool OAuthDynamicRegistration { get; set; } = true;

    /// <summary>Configured clients, keyed by client_id, when registration is disabled or for stable clients.</summary>
    public Dictionary<string, McpOAuthClient> OAuthClients { get; set; } = [];

    /// <summary>Maximum registrations, consent requests and codes held at once per category.</summary>
    public int OAuthMaxPending { get; set; } = 1000;

    /// <summary>Minutes an unapproved dynamic client registration occupies capacity.</summary>
    public int OAuthRegistrationMinutes { get; set; } = 15;

    /// <summary>Minutes a staff consent form remains valid.</summary>
    public int OAuthAuthorizationMinutes { get; set; } = 10;

    /// <summary>Minutes an authorization code remains valid.</summary>
    public int OAuthCodeMinutes { get; set; } = 2;

    /// <summary>Calls one token may make a minute; more are answered 429 until the minute is over.</summary>
    public int RequestsPerMinutePerToken { get; set; } = 60;

    /// <summary>How many days a new token lasts when its maker does not say.</summary>
    public int DefaultTokenDays { get; set; } = 30;

    /// <summary>The longest a token may last, in days.</summary>
    public int MaxTokenDays { get; set; } = 365;

    /// <summary>How much of a call's arguments the call log keeps, in characters.</summary>
    public int AuditArgumentChars { get; set; } = 2000;
}
