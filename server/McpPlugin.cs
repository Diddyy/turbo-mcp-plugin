using Microsoft.Extensions.DependencyInjection;
using Turbo.Contracts.Plugins;
using Turbo.Contracts.Plugins.Admin;

namespace TurboMcp;

/// <summary>
/// The hotel's MCP server: AI assistants call every live plugin's tools (turbo-cloud
/// docs/plugin-mcp-tools.md) at <c>/api/plugins/mcp/mcp</c>, as the staff member whose token they
/// hold. Its own tools, in <c>Tools/</c>, read the hotel and change nothing.
/// </summary>
public sealed class McpPlugin : TurboPlugin
{
    protected override void Services(IServiceCollection services)
    {
        services.AddSingleton<McpTokens>();
        services.AddSingleton<McpAudit>();
        services.AddSingleton<McpServer>();
        services.AddSingleton<IPluginAdmin, McpAdmin>();
    }
}
