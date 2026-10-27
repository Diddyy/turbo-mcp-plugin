namespace TurboMcp;

/// <summary>
/// The plugin's permission nodes, declared by <see cref="McpAdmin"/> (<c>plugin.mcp.*</c>) and
/// granted to no one by default. Each tool has its own, so staff are given tools one by one.
/// </summary>
public static class McpNodes
{
    /// <summary>Make, use and revoke one's own tokens. A token whose member loses it stops working.</summary>
    public const string USE = "plugin.mcp.use";

    /// <summary>See and revoke every member's tokens, and read the call log.</summary>
    public const string MANAGE = "plugin.mcp.manage";

    public const string FIND_PLAYER = "plugin.mcp.tool.find_player";
    public const string PLAYER_DETAILS = "plugin.mcp.tool.player_details";
    public const string ROOM_STATE = "plugin.mcp.tool.room_state";
    public const string CATALOG_SEARCH = "plugin.mcp.tool.catalog_search";
    public const string OPEN_REPORTS = "plugin.mcp.tool.open_reports";
    public const string HOTEL_STATUS = "plugin.mcp.tool.hotel_status";
    public const string PLUGIN_STATES = "plugin.mcp.tool.plugin_states";

    /// <summary>The tool nodes, as <see cref="McpAdmin"/> declares them: the name after <c>plugin.mcp.</c> and what it allows.</summary>
    public static readonly (string Name, string Description)[] TOOLS =
    [
        ("tool.find_player", "MCP: find players by name (id, name, motto, online)"),
        ("tool.player_details", "MCP: a player's wallet, club, room and furni counts"),
        ("tool.room_state", "MCP: a room's owner, users, furni counts and wired errors"),
        ("tool.catalog_search", "MCP: search the catalog's offers and pages"),
        ("tool.open_reports", "MCP: the moderation queue's open reports"),
        ("tool.hotel_status", "MCP: online count, open or closed, server memory"),
        ("tool.plugin_states", "MCP: plugins' states, errors and contributions"),
    ];
}
