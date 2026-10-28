using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Plugins;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Contracts.Plugins.Web;
using Turbo.Database.Delegates;
using Turbo.Primitives.Players.Permissions;
using Turbo.Testing;
using TurboMcp.Database;
using TurboMcp.Tools;

namespace TurboMcp.Tests;

/// <summary>A tool that changes something, which a read-only token is never offered.</summary>
[RequiresPermission("test.warn")]
public sealed class WarnTool : McpTool<WarnInput>
{
    public override string Name => "warn_player";

    public override string Description => "Warns a player.";

    public override Task<McpToolResult> HandleAsync(
        WarnInput input,
        McpToolContext ctx,
        CancellationToken ct
    ) => Task.FromResult(McpToolResult.Text($"Warned {input.Id}."));
}

public sealed record WarnInput(int Id);

/// <summary>
/// The MCP server as the hotel runs it, without one: its tables in SQLite, the hotel in memory,
/// the tools through the test kit's registry (find_player and a tool that changes something).
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;

    public TestHost(McpSettings? settings = null)
    {
        _connection.Open();

        var services = new ServiceCollection();

        services.AddSingleton<TablePrefixProvider>(() => "mcp_");
        services.AddDbContext<McpDbContext>(x => x.UseSqlite(_connection));
        services.AddSingleton<Turbo.Contracts.Hotel.IHotel>(Hotel);
        services.AddSingleton<IOptionsMonitor<McpSettings>>(
            new StaticOptions<McpSettings>(settings ?? new McpSettings())
        );
        var tester = new McpToolTester(
            new FindPlayerTool(Hotel),
            new WarnTool(),
            new AlertPlayerTool(Hotel),
            new KickFromRoomTool(Hotel)
        )
            .WithResources(new TurboMcp.Resources.PlayerResource(Hotel))
            .WithPrompts(
                new TurboMcp.Prompts.SummariseReportsPrompt(),
                new TurboMcp.Prompts.InvestigateWiredPrompt(Hotel)
            );
        var registry = tester.Registry(Hotel);
        services.AddSingleton<IMcpTools>(registry);
        services.AddSingleton<IMcpResources>(registry);
        services.AddSingleton<IMcpPrompts>(registry);
        services.AddSingleton(
            new PluginManifest
            {
                Name = "MCP",
                Key = "mcp",
                Version = "1.0.0",
                Author = "Turbo",
            }
        );
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Turbo:Web:SiteUrl"] = "https://hotel.test" }
                )
                .Build()
        );
        services.AddSingleton<McpTokens>();
        services.AddSingleton<McpOAuth>();
        services.AddSingleton<McpAudit>();
        services.AddSingleton<McpServer>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();

        scope.ServiceProvider.GetRequiredService<McpDbContext>().Database.EnsureCreated();
    }

    public FakeHotel Hotel { get; } =
        new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    public McpTokens Tokens => _services.GetRequiredService<McpTokens>();

    public McpOAuth OAuth => _services.GetRequiredService<McpOAuth>();

    public McpAudit Audit => _services.GetRequiredService<McpAudit>();

    public McpServer Server => _services.GetRequiredService<McpServer>();

    /// <summary>A staff member who may use MCP, holding the tool nodes given.</summary>
    public FakePlayer Staff(string name, params string[] nodes)
    {
        var staff = Hotel.Players.Add(name);

        staff.Permissions.Add(McpNodes.USE);
        staff.Permissions.UnionWith(nodes);

        return staff;
    }

    public async Task<string> TokenAsync(
        FakePlayer staff,
        bool readOnly = false,
        int? days = null
    ) => (await Tokens.CreateAsync(staff, "test", readOnly, days, CancellationToken.None)).Secret;

    /// <summary>POSTs one JSON-RPC message (an object, or JSON text) as the endpoint gets it.</summary>
    public Task<PluginWebResult> PostAsync(
        string? token,
        object message,
        Dictionary<string, string>? headers = null
    )
    {
        var body = message is string json
            ? JsonSerializer.Deserialize<JsonElement>(json)
            : JsonSerializer.SerializeToElement(message);
        var all = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase);

        if (token is not null)
            all["authorization"] = $"Bearer {token}";

        return Server.HandleAsync(
            new PluginWebRequest(
                "POST",
                "mcp",
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                body,
                null,
                "127.0.0.1"
            )
            {
                Headers = all,
            },
            CancellationToken.None
        );
    }

    /// <summary>A request's answer, as the JSON the host writes.</summary>
    public static JsonElement Json(PluginWebResult result) =>
        JsonSerializer.SerializeToElement(result.Value);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
