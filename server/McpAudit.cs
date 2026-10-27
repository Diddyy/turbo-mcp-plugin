using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Hotel;
using TurboMcp.Database;

namespace TurboMcp;

/// <summary>
/// The call log (every tool call: who, which token, the tool, its arguments, how it went, when)
/// and the rate limit per token.
/// </summary>
public sealed class McpAudit(
    IServiceScopeFactory scopes,
    IHotel hotel,
    IOptionsMonitor<McpSettings> settings
)
{
    private readonly ConcurrentDictionary<int, (long Minute, int Count)> _minutes = new();

    /// <summary>Counts a call of the token; false when it has made all its calls this minute.</summary>
    public bool TryTake(int tokenId)
    {
        var minute = hotel.Clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var limit = Math.Max(1, settings.CurrentValue.RequestsPerMinutePerToken);
        var now = _minutes.AddOrUpdate(
            tokenId,
            _ => (minute, 1),
            (_, was) => was.Minute == minute ? (minute, was.Count + 1) : (minute, 1)
        );

        return now.Count <= limit;
    }

    public async Task WriteAsync(
        McpTokenGrant grant,
        string tool,
        string arguments,
        string status,
        int durationMs,
        CancellationToken ct
    )
    {
        var keep = Math.Clamp(settings.CurrentValue.AuditArgumentChars, 0, 2000);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();

        db.Calls.Add(
            new McpCallEntity
            {
                TokenId = grant.TokenId,
                PlayerId = grant.Player.Value,
                Tool = tool.Length <= 64 ? tool : tool[..64],
                Arguments = arguments.Length <= keep ? arguments : arguments[..keep],
                Status = status,
                DurationMs = durationMs,
                CreatedAt = hotel.Clock.GetUtcNow().UtcDateTime,
            }
        );
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A page of calls, newest first, of one tool or member when given.</summary>
    public async Task<(IReadOnlyList<McpCallEntity> Items, int Total)> ListAsync(
        string? tool,
        int? player,
        int page,
        int size,
        CancellationToken ct
    )
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
        var query = db.Calls.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(tool))
            query = query.Where(x => x.Tool == tool);

        if (player is { } only)
            query = query.Where(x => x.PlayerId == only);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.Id)
            .Skip(Math.Max(0, page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return (items, total);
    }
}
