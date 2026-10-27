using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Logging;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools.Dev;

/// <summary>The least a log line is; the server's own levels.</summary>
public enum LogLevelInput
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record ServerLogInput(
    [Description("How many lines, newest first.")] [Range(1, 200)] int Take = 50,
    [Description("The least level a line is.")] LogLevelInput Level = LogLevelInput.Information,
    [Description(
        "Only lines whose category, text or exception holds this (a plugin's name, a class, a word)."
    )]
    [StringLength(200)]
        string? Contains = null
);

public sealed record ServerLogOutput(IReadOnlyList<LogLine> Lines, LogLine? LastError, int Kept);

/// <summary>One log line; <see cref="Exception"/> with its stack trace (a plugin's frames name its file and line).</summary>
public sealed record LogLine(
    long Sequence,
    DateTime AtUtc,
    string Level,
    string Category,
    string Message,
    string? Exception
)
{
    public static LogLine Of(ServerLogEntry entry) =>
        new(
            entry.Sequence,
            entry.AtUtc,
            entry.Level.ToString(),
            entry.Category,
            entry.Message,
            entry.Exception
        );
}

/// <summary>For plugin authors on their own hotel: the server's latest log lines and its last error.</summary>
[RequiresPermission(McpNodes.SERVER_LOG)]
public sealed class ServerLogTool(IServerLog log) : McpTool<ServerLogInput, ServerLogOutput>
{
    public override string Name => "server_log";

    public override string Title => "Server log";

    public override string Description =>
        "The hotel server's latest log lines, newest first (kept in memory, as the console shows them), filtered by level and text, and its last error with the exception's stack trace. Use after a plugin reload, a failed action or an exception, to see what the server said. For a developer's own hotel.";

    public override bool ReadOnly => true;

    /// <summary>For a developer's own hotel: not registered on a live one.</summary>
    public override bool DevelopmentOnly => true;

    public override Task<McpToolResult> HandleAsync(
        ServerLogInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var lines = log.Recent(input.Take, (LogLevel)input.Level, input.Contains);
        var last = log.LastError is { } error ? LogLine.Of(error) : null;

        return Task.FromResult(
            Ok(
                new ServerLogOutput([.. lines.Select(LogLine.Of)], last, log.Capacity),
                lines.Count == 0 ? "No line fits; the server keeps its newest lines only." : null
            )
        );
    }
}
