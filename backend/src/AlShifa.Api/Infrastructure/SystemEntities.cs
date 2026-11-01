using System.ComponentModel.DataAnnotations;

namespace AlShifa.Api.Infrastructure;

public class AuditLog
{
    public long Id { get; set; }

    public long? UserId { get; set; }

    [MaxLength(80)]
    public string Action { get; set; } = "";

    [MaxLength(80)]
    public string Entity { get; set; } = "";

    [MaxLength(50)]
    public string? EntityId { get; set; }

    [MaxLength(2000)]
    public string? Details { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class OutboxEvent
{
    public long Id { get; set; }

    [MaxLength(60)]
    public string Type { get; set; } = "";

    /// <summary>Identifiers only, never patient data.</summary>
    [MaxLength(500)]
    public string Payload { get; set; } = "{}";

    /// <summary>Comma-separated SignalR groups allowed to receive the event.</summary>
    [MaxLength(300)]
    public string Groups { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ProcessedAt { get; set; }
}

public class IdempotencyRecord
{
    public long Id { get; set; }

    public long UserId { get; set; }

    [MaxLength(64)]
    public string Key { get; set; } = "";

    [MaxLength(64)]
    public string RequestHash { get; set; } = "";

    public int StatusCode { get; set; }

    public string ResponseBody { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class NumberSequence
{
    [MaxLength(40)]
    public string Scope { get; set; } = "";

    [MaxLength(10)]
    public string Period { get; set; } = "";

    public long Value { get; set; }
}

public class Setting
{
    [MaxLength(80)]
    public string Key { get; set; } = "";

    [MaxLength(2000)]
    public string Value { get; set; } = "";

    public int Version { get; set; }
}
