using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboMcp.Database;

/// <summary>
/// A staff member's token for an AI assistant: the assistant acts as that member, with their
/// permission nodes, until the token expires or is revoked. Only the SHA-256 of the token is kept.
/// </summary>
[Table("tokens")]
[Index(nameof(Hash), IsUnique = true)]
[Index(nameof(PlayerId))]
public class McpTokenEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    /// <summary>The staff member the token acts as, who made it.</summary>
    [Column("player_id")]
    public int PlayerId { get; set; }

    /// <summary>What the member called it ("Claude Code on my laptop").</summary>
    [Column("label")]
    [MaxLength(64)]
    public string Label { get; set; } = string.Empty;

    /// <summary>The token's SHA-256, in lower-case hex.</summary>
    [Column("hash")]
    [MaxLength(64)]
    public string Hash { get; set; } = string.Empty;

    /// <summary>The first characters of the token, so staff can tell tokens apart.</summary>
    [Column("prefix")]
    [MaxLength(16)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>The assistant is offered, and may call, read-only tools only.</summary>
    [Column("read_only")]
    public bool ReadOnly { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>Who revoked it: the member, or staff managing every token.</summary>
    [Column("revoked_by")]
    public int? RevokedBy { get; set; }
}
