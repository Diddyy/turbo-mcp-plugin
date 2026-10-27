using Turbo.Contracts.Plugins;

namespace TurboMcp;

/// <summary>The MCP server's limits.</summary>
[PluginSettings("Mcp")]
public sealed class McpSettings
{
    /// <summary>Calls one token may make a minute; more are answered 429 until the minute is over.</summary>
    public int RequestsPerMinutePerToken { get; set; } = 60;

    /// <summary>How many days a new token lasts when its maker does not say.</summary>
    public int DefaultTokenDays { get; set; } = 30;

    /// <summary>The longest a token may last, in days.</summary>
    public int MaxTokenDays { get; set; } = 365;

    /// <summary>How much of a call's arguments the call log keeps, in characters.</summary>
    public int AuditArgumentChars { get; set; } = 2000;
}
