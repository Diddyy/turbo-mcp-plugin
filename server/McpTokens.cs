using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Hotel;
using Turbo.Primitives.Players;
using TurboMcp.Database;

namespace TurboMcp;

/// <summary>A token that signs a call in: the member it acts as and whether it is read-only.</summary>
public sealed record McpTokenGrant(int TokenId, PlayerId Player, bool ReadOnly);

/// <summary>
/// Staff tokens for AI assistants. A token is made by the member it acts as, shown to them once,
/// and kept only as its SHA-256; it works until it expires or is revoked, and only while its
/// member holds <see cref="McpNodes.USE"/>.
/// </summary>
public sealed class McpTokens(
    IServiceScopeFactory scopes,
    IHotel hotel,
    IOptionsMonitor<McpSettings> settings
)
{
    /// <summary>What every token starts with, so a leaked one is recognised.</summary>
    public const string PREFIX = "tmcp_";

    /// <summary>How long a new token is shown to its maker.</summary>
    public static readonly TimeSpan REVEAL = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<int, (string Secret, int Maker, DateTime Until)> _new =
        new();

    private DateTime Now => hotel.Clock.GetUtcNow().UtcDateTime;

    /// <summary>Makes a token for <paramref name="player"/>; its secret is answered here, then only by <see cref="Reveal"/>.</summary>
    public async Task<(McpTokenEntity Token, string Secret)> CreateAsync(
        PlayerId player,
        string label,
        bool readOnly,
        int? days,
        CancellationToken ct
    )
    {
        var limits = settings.CurrentValue;
        var lasts = Math.Clamp(
            days ?? limits.DefaultTokenDays,
            1,
            Math.Max(1, limits.MaxTokenDays)
        );
        var secret = PREFIX + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var named = label.Trim();
        var token = new McpTokenEntity
        {
            PlayerId = player.Value,
            Label = named.Length == 0 ? "Assistant" : named[..Math.Min(64, named.Length)],
            Hash = Hash(secret),
            Prefix = secret[..(PREFIX.Length + 4)],
            ReadOnly = readOnly,
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(lasts),
        };

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();

        db.Tokens.Add(token);
        await db.SaveChangesAsync(ct);

        _new[token.Id] = (secret, player.Value, Now + REVEAL);

        return (token, secret);
    }

    /// <summary>Prevents a directly delivered OAuth token from being revealed in the panel.</summary>
    public void Hide(int id) => _new.TryRemove(id, out _);

    /// <summary>The new token's secret, to the member who made it and only for <see cref="REVEAL"/>.</summary>
    public string? Reveal(int id, int player)
    {
        foreach (var gone in _new.Where(x => x.Value.Until < Now).Select(x => x.Key).ToList())
            _new.TryRemove(gone, out _);

        return _new.TryGetValue(id, out var made) && made.Maker == player ? made.Secret : null;
    }

    /// <summary>
    /// The token in an <c>Authorization: Bearer</c> header, when it works now: not expired, not
    /// revoked, its member still holding <see cref="McpNodes.USE"/>. Notes its use.
    /// </summary>
    public async Task<McpTokenGrant?> FindAsync(string? authorization, CancellationToken ct)
    {
        if (
            authorization is null
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
            return null;

        var secret = authorization["Bearer ".Length..].Trim();

        if (!secret.StartsWith(PREFIX, StringComparison.Ordinal))
            return null;

        var hash = Hash(secret);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
        var token = await db.Tokens.FirstOrDefaultAsync(x => x.Hash == hash, ct);

        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= Now)
            return null;

        if (!await hotel.Players.HasPermissionAsync(token.PlayerId, McpNodes.USE, ct))
            return null;

        // At most once a minute: a busy assistant's every call need not write.
        if (token.LastUsedAt is not { } last || Now - last > TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = Now;
            await db.SaveChangesAsync(ct);
        }

        return new McpTokenGrant(token.Id, token.PlayerId, token.ReadOnly);
    }

    /// <summary>A page of tokens, newest first: one member's, or everyone's for null.</summary>
    public async Task<(IReadOnlyList<McpTokenEntity> Items, int Total)> ListAsync(
        int? player,
        int page,
        int size,
        CancellationToken ct
    )
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
        var query = db.Tokens.AsNoTracking();

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

    public async Task<McpTokenEntity?> GetAsync(int id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();

        return await scope
            .ServiceProvider.GetRequiredService<McpDbContext>()
            .Tokens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    /// <summary>Revokes the token at once; false when there is no such token or it was revoked.</summary>
    public async Task<bool> RevokeAsync(int id, int by, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
        var token = await db.Tokens.FirstOrDefaultAsync(x => x.Id == id, ct);

        if (token is null || token.RevokedAt is not null)
            return false;

        token.RevokedAt = Now;
        token.RevokedBy = by;
        await db.SaveChangesAsync(ct);
        _new.TryRemove(id, out _);

        return true;
    }

    /// <summary>The token's SHA-256 in lower-case hex: a token is 32 random bytes, so it needs no slow hash.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
