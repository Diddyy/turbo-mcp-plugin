using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Hotel;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;
using Turbo.Primitives.Plugins;

namespace TurboMcp.Tools;

public sealed record HotelStatusOutput(
    DateTime ServerTimeUtc,
    int Online,
    int ActiveToday,
    string Phase,
    DateTime? PhaseEndsAtUtc,
    bool LoginClosed,
    long ManagedMemoryMb,
    long WorkingSetMb,
    int ThreadPoolThreads,
    long PendingWorkItems,
    int PluginsLoaded,
    int PluginsFailed
);

/// <summary>How the hotel is now: players online, open or closed, the server's memory and plugins.</summary>
[RequiresPermission(McpNodes.HOTEL_STATUS)]
public sealed class HotelStatusTool(IHotel hotel, IPluginAdministration plugins)
    : McpTool<McpNoInput, HotelStatusOutput>
{
    public override string Name => "hotel_status";

    public override string Title => "Hotel status";

    public override string Description =>
        "The hotel now: players online and active in the last day, whether it is open, in maintenance or shutting down (and when that ends), the server's memory and thread pool, and how many plugins are loaded or failed (plugin_states says which).";

    public override bool ReadOnly => true;

    public override async Task<McpToolResult> HandleAsync(
        McpNoInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var now = hotel.Clock.GetUtcNow().UtcDateTime;
        var status = hotel.Status.Current;
        var list = plugins.List();

        return Ok(
            new HotelStatusOutput(
                now,
                hotel.Players.OnlineIds().Count,
                await hotel.Players.CountAsync(now.AddDays(-1), ct),
                status.Phase.ToString(),
                status.AtUtc,
                status.BlocksLogin,
                GC.GetTotalMemory(false) / (1024 * 1024),
                Environment.WorkingSet / (1024 * 1024),
                ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount,
                list.Count(x => x.State == PluginStateType.Loaded),
                list.Count(x => x.State is PluginStateType.Failed or PluginStateType.Rejected)
            )
        );
    }
}
