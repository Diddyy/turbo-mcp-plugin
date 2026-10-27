using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Web;

namespace TurboMcp;

/// <summary>
/// <c>POST /api/plugins/mcp/mcp</c>: the MCP endpoint. Anonymous to the host, which signs players
/// in by its own tokens: this one reads the caller's staff token itself (<see cref="McpServer"/>).
/// </summary>
public sealed class McpRoute(McpServer server) : IPluginWebRoute
{
    public string Method => "POST";

    public string Pattern => "mcp";

    public bool Anonymous => true;

    public bool CredentialHeaders => true;

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        server.HandleAsync(request, ct);
}

/// <summary>
/// <c>GET /api/plugins/mcp/mcp</c>: the transport's event stream, which a stateless server does not
/// offer. 405, as the specification asks of a server without one.
/// </summary>
public sealed class McpStreamRoute : IPluginWebRoute
{
    public string Method => "GET";

    public string Pattern => "mcp";

    public bool Anonymous => true;

    // An assistant sends its staff token here too: the host must not answer it 401 as a stale player token.
    public bool CredentialHeaders => true;

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        Task.FromResult(
            new PluginWebResult(
                405,
                Error: "This MCP server answers POST only; it has no event stream."
            )
        );
}
