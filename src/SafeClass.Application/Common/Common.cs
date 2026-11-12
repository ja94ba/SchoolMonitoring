using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Common;

public static class TimeExtensions
{
    /// <summary>Converts a stored UTC DateTime (Kind may be Unspecified after reading from SQLite) to JavaScript milliseconds.</summary>
    public static long ToUnixMs(this DateTime dt) =>
        new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static long? ToUnixMs(this DateTime? dt) => dt.HasValue ? dt.Value.ToUnixMs() : null;

    public static DateTime AsUtc(this DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
}

public static class Paging
{
    public const int MaxPageSize = 200;

    public static (int page, int pageSize) Normalize(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;
        return (page, pageSize);
    }
}

/// <summary>
/// Classroom status rules. Precedence: Emergency (any unresolved alert) > Offline (no active device,
/// or device not reporting) > Normal. The emergency condition and the connectivity condition are
/// stored separately (alerts vs. Device.IsOnline); this is the only place they are combined.
/// </summary>
public static class StatusEngine
{
    public static ClassroomStatus Compute(bool hasUnresolvedAlerts, Device? device)
    {
        if (hasUnresolvedAlerts) return ClassroomStatus.Emergency;
        if (device is null || !device.IsActive || !device.IsOnline) return ClassroomStatus.Offline;
        return ClassroomStatus.Normal;
    }

    /// <summary>Applies a status. Records a history row only when the status actually changes.</summary>
    public static bool Apply(Interfaces.ISafeClassDbContext db, Classroom room, ClassroomStatus newStatus, DateTime now,
        string source, Guid? userId, Guid? alertId, string? reason)
    {
        if (room.CurrentStatus == newStatus) return false;

        db.ClassroomStatusHistory.Add(new ClassroomStatusHistory
        {
            ClassroomId = room.Id,
            PreviousStatus = room.CurrentStatus,
            NewStatus = newStatus,
            ChangedAtUtc = now,
            TriggerSource = source,
            TriggeredByUserId = userId,
            RelatedAlertId = alertId,
            Reason = reason
        });
        room.CurrentStatus = newStatus;
        room.LastStatusUpdateUtc = now;
        room.UpdatedAtUtc = now;
        return true;
    }
}

public static class Mapping
{
    public static DTOs.ClassroomDto ToDto(this Classroom c, Device? d) => new()
    {
        Id = c.Id,
        SchoolId = c.SchoolId,
        Code = c.Code,
        Name = c.Name,
        Building = c.Building,
        Floor = c.Floor,
        Adviser = c.Adviser,
        Device = d?.DeviceIdentifier,
        DeviceId = d?.Id,
        FirmwareVersion = d?.FirmwareVersion,
        Signal = d is { IsOnline: true } ? d.SignalStrength : 0,
        Status = c.CurrentStatus,
        LastUpdate = Latest(c.LastStatusUpdateUtc, d?.LastHeartbeatUtc).ToUnixMs(),
        IsActive = c.IsActive
    };

    public static DTOs.DeviceDto ToDto(this Device d, string? classroomCode) => new()
    {
        Id = d.Id,
        ClassroomId = d.ClassroomId,
        ClassroomCode = classroomCode,
        DeviceIdentifier = d.DeviceIdentifier,
        DeviceName = d.DeviceName,
        DeviceType = d.DeviceType,
        FirmwareVersion = d.FirmwareVersion,
        SignalStrength = d.SignalStrength,
        IsOnline = d.IsOnline,
        LastHeartbeat = d.LastHeartbeatUtc.ToUnixMs(),
        RegisteredAt = d.RegisteredAtUtc.ToUnixMs(),
        IsActive = d.IsActive,
        CredentialRevoked = d.CredentialRevokedAtUtc.HasValue
    };

    public static string ActivityKind(string eventType) => eventType switch
    {
        ActivityEventTypes.AlertCreated or ActivityEventTypes.AlertAcknowledged => "emergency",
        ActivityEventTypes.DeviceOffline => "offline",
        _ => "normal"
    };

    private static DateTime Latest(DateTime a, DateTime? b) => b.HasValue && b.Value > a ? b.Value : a;
}
