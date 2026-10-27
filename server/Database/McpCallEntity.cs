using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboMcp.Database;

/// <summary>One tool call an assistant made: who, through which token, the tool, its arguments, how it went and when.</summary>
[Table("calls")]
[Index(nameof(CreatedAt))]
[Index(nameof(TokenId))]
public class McpCallEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("token_id")]
    public int TokenId { get; set; }

    [Column("player_id")]
    public int PlayerId { get; set; }

    [Column("tool")]
    [MaxLength(64)]
    public string Tool { get; set; } = string.Empty;

    /// <summary>The arguments as JSON, cut to the setting's length.</summary>
    [Column("arguments")]
    [MaxLength(2000)]
    public string Arguments { get; set; } = string.Empty;

    /// <summary><c>ok</c>, <c>error</c>, <c>invalid</c>, <c>unavailable</c>, <c>failed</c> or <c>limited</c>.</summary>
    [Column("status")]
    [MaxLength(16)]
    public string Status { get; set; } = string.Empty;

    [Column("duration_ms")]
    public int DurationMs { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
