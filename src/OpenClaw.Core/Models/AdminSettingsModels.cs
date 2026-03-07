using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

public sealed class AdminSettingsSnapshot
{
    public string UsageFooter { get; init; } = "off";
    public int MaxConcurrentSessions { get; init; }
    public int SessionTimeoutMinutes { get; init; }
    public long SessionTokenBudget { get; init; }
    public int SessionRateLimitPerMinute { get; init; }
    public bool AllowQueryStringToken { get; init; }
    public int BrowserSessionIdleMinutes { get; init; }
    public int BrowserRememberDays { get; init; }
    public string AutonomyMode { get; init; } = "supervised";
    public bool RequireToolApproval { get; init; }
    public int ToolApprovalTimeoutSeconds { get; init; }
    public bool ParallelToolExecution { get; init; }
    public bool AllowShell { get; init; }
    public bool ReadOnlyMode { get; init; }
    public bool EnableBrowserTool { get; init; }
    public bool AllowBrowserEvaluate { get; init; }
    public int MaxHistoryTurns { get; init; }
    public bool EnableCompaction { get; init; }
    public int CompactionThreshold { get; init; }
    public int CompactionKeepRecent { get; init; }
    public bool RetentionEnabled { get; init; }
    public bool RetentionRunOnStartup { get; init; }
    public int RetentionSweepIntervalMinutes { get; init; }
    public int RetentionSessionTtlDays { get; init; }
    public int RetentionBranchTtlDays { get; init; }
    public bool RetentionArchiveEnabled { get; init; }
    public int RetentionArchiveRetentionDays { get; init; }
    public int RetentionMaxItemsPerSweep { get; init; }
    public string AllowlistSemantics { get; init; } = "legacy";
    public bool SmsEnabled { get; init; }
    public bool SmsValidateSignature { get; init; }
    public string SmsDmPolicy { get; init; } = "pairing";
    public bool TelegramEnabled { get; init; }
    public bool TelegramValidateSignature { get; init; }
    public string TelegramDmPolicy { get; init; } = "pairing";
    [JsonPropertyName("whatsappEnabled")]
    public bool WhatsAppEnabled { get; init; }
    [JsonPropertyName("whatsappValidateSignature")]
    public bool WhatsAppValidateSignature { get; init; }
    [JsonPropertyName("whatsappDmPolicy")]
    public string WhatsAppDmPolicy { get; init; } = "pairing";
}

public sealed class AdminSettingsPersistenceInfo
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public DateTimeOffset? LastModifiedAtUtc { get; init; }
}
