using SafeClass.Domain.Entities;

namespace SafeClass.Application.Options;

public class SafeClassOptions
{
    public const string Section = "SafeClass";

    /// <summary>A device with no heartbeat for this long is marked offline (once, not on every missed beat).</summary>
    public int HeartbeatOfflineThresholdSeconds { get; set; } = 60;

    /// <summary>How often the background monitor looks for silent devices.</summary>
    public int OfflineCheckIntervalSeconds { get; set; } = 15;

    /// <summary>Heartbeat history older than this is purged. Incident history is never purged. 0 = keep forever.</summary>
    public int HeartbeatHistoryRetentionDays { get; set; } = 30;

    /// <summary>At most one heartbeat history row per device per interval (state changes are always stored).</summary>
    public int HeartbeatHistoryMinIntervalSeconds { get; set; } = 60;

    /// <summary>Enables Simulate Alert / Reset Demo Data and the demo heartbeat simulator. Keep false in production.</summary>
    public bool DemoModeEnabled { get; set; }

    public string[] AllowedAlertTypes { get; set; } = AlertTypes.Defaults;
}
