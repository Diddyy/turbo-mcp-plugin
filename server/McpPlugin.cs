using Microsoft.Extensions.DependencyInjection;
using Turbo.Contracts.Plugins;
using Turbo.Contracts.Plugins.Admin;
using TurboMcp.Tools.Dev;

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
        // The dev tools' own: the server's guides, a plugin's tables (their columns, a SELECT over
        // them) and the client errors plugin's console errors.
        services.AddSingleton(_ => new DevDocs());
        services.AddSingleton<ITableColumns, MySqlTableColumns>();
        services.AddSingleton<IPluginQueries, MySqlPluginQueries>();
        services.AddSingleton<IClientConsoleErrors, MySqlClientConsoleErrors>();
    }
}
