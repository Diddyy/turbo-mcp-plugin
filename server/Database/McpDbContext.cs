using Microsoft.EntityFrameworkCore;
using Turbo.Database.Context;
using Turbo.Database.Delegates;

namespace TurboMcp.Database;

/// <summary>The plugin's tables, under its prefix (<c>mcp_</c>): staff tokens and the call log. No foreign keys into core.</summary>
public class McpDbContext : PluginDbContextBase<McpDbContext>
{
    /// <summary>As the host makes it when the plugin loads, and as <c>dotnet ef</c> does.</summary>
    public McpDbContext() { }

    /// <summary>Over a connection of the caller's (the tests' in-memory database).</summary>
    public McpDbContext(DbContextOptions<McpDbContext> options, TablePrefixProvider prefix)
        : base(options, prefix) { }

    public DbSet<McpTokenEntity> Tokens => Set<McpTokenEntity>();

    public DbSet<McpCallEntity> Calls => Set<McpCallEntity>();
}
