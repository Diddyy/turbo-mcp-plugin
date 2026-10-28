namespace TurboMcp;

/// <summary>A configured public OAuth client with exact redirect addresses.</summary>
public sealed class McpOAuthClient
{
    public string Name { get; set; } = "Assistant";
    public string[] RedirectUris { get; set; } = [];
}
