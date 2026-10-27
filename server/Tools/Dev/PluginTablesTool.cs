using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;
using Turbo.Primitives.Plugins;
using TurboMcp.Database;

namespace TurboMcp.Tools.Dev;

public sealed record PluginTablesInput(
    [Description("The plugin's key (its manifest's Key).")]
    [StringLength(64, MinimumLength = 1)]
        string Key
);

public sealed record PluginTablesOutput(string Key, IReadOnlyList<PluginTable> Tables);

/// <summary>One of the plugin's tables as the database has it, its rows counted.</summary>
public sealed record PluginTable(string Name, long Rows, IReadOnlyList<TableColumn> Columns);

/// <summary>A column: its SQL type, whether it takes null, its key (<c>PRI</c>, <c>UNI</c>, <c>MUL</c>), default and extra (<c>auto_increment</c>).</summary>
public sealed record TableColumn(
    string Name,
    string Type,
    bool Nullable,
    string? Key,
    string? Default,
    string? Extra
);

/// <summary>The columns of tables, read from the database's own catalogue.</summary>
public interface ITableColumns
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<TableColumn>>> OfAsync(
        IReadOnlyCollection<string> tables,
        CancellationToken ct
    );
}

/// <summary>
/// The hotel's database's <c>information_schema.COLUMNS</c>, over this plugin's own connection:
/// one fixed query for the tables named, never one of the caller's.
/// </summary>
public sealed class MySqlTableColumns(IServiceScopeFactory scopes) : ITableColumns
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TableColumn>>> OfAsync(
        IReadOnlyCollection<string> tables,
        CancellationToken ct
    )
    {
        var found = new Dictionary<string, List<TableColumn>>(StringComparer.OrdinalIgnoreCase);

        if (tables.Count == 0)
            return new Dictionary<string, IReadOnlyList<TableColumn>>();

        await using var scope = scopes.CreateAsyncScope();
        var connection = scope
            .ServiceProvider.GetRequiredService<McpDbContext>()
            .Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        var names = tables.Select((table, i) => (Parameter: $"@t{i}", table)).ToList();

        command.CommandText =
            "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, EXTRA "
            + "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ("
            + string.Join(", ", names.Select(x => x.Parameter))
            + ") ORDER BY TABLE_NAME, ORDINAL_POSITION";

        foreach (var (parameter, table) in names)
        {
            var p = command.CreateParameter();

            p.ParameterName = parameter;
            p.Value = table;
            command.Parameters.Add(p);
        }

        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);

            if (!found.TryGetValue(table, out var columns))
                found[table] = columns = [];

            columns.Add(
                new TableColumn(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3) == "YES",
                    Text(reader, 4),
                    Text(reader, 5),
                    Text(reader, 6)
                )
            );
        }

        return found.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<TableColumn>)x.Value,
            StringComparer.OrdinalIgnoreCase
        );
    }

    private static string? Text(DbDataReader reader, int column) =>
        reader.IsDBNull(column) || reader.GetValue(column)?.ToString() is not { Length: > 0 } text
            ? null
            : text;
}

/// <summary>For plugin authors: a plugin's tables, as the hotel's database has them now.</summary>
[RequiresPermission(McpNodes.PLUGIN_TABLES)]
public sealed class PluginTablesTool(IPluginAdministration plugins, ITableColumns columns)
    : McpTool<PluginTablesInput, PluginTablesOutput>
{
    public override string Name => "plugin_tables";

    public override string Title => "A plugin's tables";

    public override string Description =>
        "One plugin's tables in the hotel's database (those under its manifest's TablePrefix, its migrations' history included): each with its rows counted and its columns (SQL type, null, key, default, extra), as the database has them now. Use to check a migration, or before writing a query in plugin code. Reads no rows.";

    public override bool ReadOnly => true;

    /// <summary>For a developer's own hotel: not registered on a live one.</summary>
    public override bool DevelopmentOnly => true;

    public override async Task<McpToolResult> HandleAsync(
        PluginTablesInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        if (await plugins.PlanUninstallAsync(input.Key.Trim(), ct) is not { } plan)
            return McpToolResult.Error(
                $"No plugin has the key \"{input.Key}\". The keys are {string.Join(", ", plugins.List().Select(x => x.Key).Order())}."
            );

        var tables = plan.Data.Where(x => x.Kind == PluginDataKind.Table).ToList();

        if (tables.Count == 0)
            return McpToolResult.Text(
                $"{plan.Plugin.Key} has no tables: its manifest names no TablePrefix, or no table starts with it yet (its migrations run as it loads)."
            );

        var found = await columns.OfAsync([.. tables.Select(x => x.Name)], ct);

        return Ok(
            new PluginTablesOutput(
                plan.Plugin.Key,
                [
                    .. tables.Select(x => new PluginTable(
                        x.Name,
                        x.Count,
                        found.TryGetValue(x.Name, out var c) ? c : []
                    )),
                ]
            )
        );
    }
}
