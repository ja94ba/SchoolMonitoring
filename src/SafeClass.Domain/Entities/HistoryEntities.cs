using SafeClass.Domain.Enums;

namespace SafeClass.Domain.Entities;

// Append-only tables. Application code only ever adds rows to these (heartbeat history
// is the one table with a configurable retention window; incident data is never deleted).

public class ClassroomStatusHistory
{
    public long Id { get; set; }
    public Guid ClassroomId { get; set; }
    public ClassroomStatus? PreviousStatus { get; set; }
    public ClassroomStatus NewStatus { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string TriggerSource { get; set; } = EventSources.System;
    public Guid? TriggeredByUserId { get; set; }
    public Guid? RelatedAlertId { get; set; }
    public string? Reason { get; set; }

    public Classroom? Classroom { get; set; }
}

public class DeviceHeartbeatHistory
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public int SignalStrength { get; set; }
    public string? ReportedStatus { get; set; }
    public string? FirmwareVersion { get; set; }
    public bool IsOnline { get; set; }
}

/// <summary>Actual online/offline transitions (not every heartbeat). Source for outage reports.</summary>
public class DeviceConnectionHistory
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public bool IsOnline { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string? Reason { get; set; }
}

public class AlertEventHistory
{
    public long Id { get; set; }
    public Guid AlertId { get; set; }
    public string EventType { get; set; } = "";
    public AlertStatus? PreviousStatus { get; set; }
    public AlertStatus NewStatus { get; set; }
    public DateTime EventTimestampUtc { get; set; }
    public Guid? PerformedByUserId { get; set; }
    public string Source { get; set; } = EventSources.System;
    public string? Notes { get; set; }
}

public class ActivityLog
{
    public long Id { get; set; }
    public Guid SchoolId { get; set; }
    public Guid? ClassroomId { get; set; }
    public Guid? AlertId { get; set; }
    public string EventType { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Source { get; set; } = EventSources.System;
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid SchoolId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public DateTime TimestampUtc { get; set; }

    /// <summary>Short, non-sensitive description. Never secrets, keys or passwords.</summary>
    public string? Details { get; set; }
}
