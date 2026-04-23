namespace TrueFluentPro.Api.Models;

public sealed class UserRecord
{
    public string Id { get; set; } = string.Empty;          // AAD oid
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Subscription { get; set; } = "free";      // free / basic / pro
    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class UsageEvent
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty; // chat_token, speech_minute, image, video_second, translate_minute
    public long Amount { get; set; }
    public string? Model { get; set; }
    public string? EndpointPath { get; set; }
    public int? StatusCode { get; set; }
    public int? DurationMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MonthlyUsage
{
    public string UserId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string YearMonth { get; set; } = string.Empty;   // "2026-04"
    public long TotalAmount { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class SubscriptionPlan
{
    public string PlanId { get; set; } = string.Empty;       // free, basic, pro
    public string DisplayName { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }
    public Dictionary<string, long> Limits { get; set; } = new(); // resource_type → monthly limit
    public bool IsActive { get; set; } = true;
}

public sealed class CredentialRecord
{
    public string Id { get; set; } = string.Empty;           // aoai_primary, speech_eastus, etc.
    public string ServiceType { get; set; } = string.Empty;  // aoai, speech, blob
    public byte[] EncryptedValue { get; set; } = [];
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class UserProfile
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Subscription { get; set; } = string.Empty;
    public Dictionary<string, QuotaInfo> Quotas { get; set; } = new();
}

public sealed class QuotaInfo
{
    public long Used { get; set; }
    public long Limit { get; set; }
    public long Remaining => Math.Max(0, Limit - Used);
}
