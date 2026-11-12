using Microsoft.EntityFrameworkCore;
using SafeClass.Application.DTOs;
using SafeClass.Application.Interfaces;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;
using SafeClass.Application.Common;

namespace SafeClass.Application.Services;

internal static class ServiceHelpers
{
    public static Task<Device?> ActiveDeviceAsync(ISafeClassDbContext db, Guid classroomId, CancellationToken ct) =>
        db.Devices.Where(d => d.ClassroomId == classroomId && d.IsActive)
            .OrderBy(d => d.RegisteredAtUtc)
            .FirstOrDefaultAsync(ct);

    public static Task<bool> HasUnresolvedAlertsAsync(ISafeClassDbContext db, Guid classroomId, Guid? excludeAlertId, CancellationToken ct) =>
        db.EmergencyAlerts.AnyAsync(a => a.ClassroomId == classroomId && a.Status != AlertStatus.Resolved
                                         && (excludeAlertId == null || a.Id != excludeAlertId), ct);

    /// <summary>Escapes % and _ so user search text is matched literally by LIKE ... ESCAPE '\'.</summary>
    public static string LikePattern(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static (DateTime? From, DateTime? ToExclusive) DateRange(DateTime? start, DateTime? end)
    {
        DateTime? from = start?.AsUtc();
        DateTime? to = null;
        if (end.HasValue)
        {
            var e = end.Value.AsUtc();
            to = e.TimeOfDay == TimeSpan.Zero ? e.AddDays(1) : e;
        }
        return (from, to);
    }

    public static async Task SafeNotifyAsync(Func<Task> action, Microsoft.Extensions.Logging.ILogger log)
    {
        try { await action(); }
        catch (Exception ex) { log.LogWarning(ex, "Real-time notification failed (data was saved successfully)."); }
    }
}

/// <summary>Device online/offline state changes. Only real transitions create history rows.</summary>
internal static class DeviceConnection
{
    /// <summary>Returns true when the device was offline before (a real transition).</summary>
    public static bool MarkOnline(ISafeClassDbContext db, Device device, DateTime now, string reason)
    {
        device.LastHeartbeatUtc = now;
        device.LastUpdatedUtc = now;
        if (device.IsOnline) return false;
        device.IsOnline = true;
        db.DeviceConnectionHistory.Add(new DeviceConnectionHistory
        {
            DeviceId = device.Id, IsOnline = true, ChangedAtUtc = now, Reason = reason
        });
        return true;
    }

    public static bool MarkOffline(ISafeClassDbContext db, Device device, DateTime now, string reason)
    {
        device.LastUpdatedUtc = now;
        if (!device.IsOnline) return false;
        device.IsOnline = false;
        db.DeviceConnectionHistory.Add(new DeviceConnectionHistory
        {
            DeviceId = device.Id, IsOnline = false, ChangedAtUtc = now, Reason = reason
        });
        return true;
    }
}

internal sealed class AlertRow
{
    public Guid Id { get; set; }
    public Guid ClassroomId { get; set; }
    public string? RoomCode { get; set; }
    public string? RoomName { get; set; }
    public string? Building { get; set; }
    public string Type { get; set; } = "";
    public AlertStatus Status { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNotes { get; set; }
    public string Source { get; set; } = "";
    public Guid? DeviceId { get; set; }

    public AlertDto ToDto() => new()
    {
        Id = Id,
        RoomId = ClassroomId,
        RoomCode = RoomCode,
        RoomName = RoomName,
        Building = Building,
        Type = Type,
        Status = Status,
        ReceivedAt = ReceivedAtUtc.ToUnixMs(),
        AckAt = AcknowledgedAtUtc.ToUnixMs(),
        AcknowledgedBy = AcknowledgedBy,
        ResolvedAt = ResolvedAtUtc.ToUnixMs(),
        ResolvedBy = ResolvedBy,
        ResolutionNotes = ResolutionNotes,
        Source = Source,
        DeviceId = DeviceId
    };
}

internal static class AlertQueries
{
    public static IQueryable<EmergencyAlert> ApplyFilter(IQueryable<EmergencyAlert> q, AlertFilter f, Guid schoolId)
    {
        q = q.Where(a => a.SchoolId == schoolId);
        if (f.ClassroomId is Guid classroomId) q = q.Where(a => a.ClassroomId == classroomId);
        if (!string.IsNullOrWhiteSpace(f.Building))
        {
            var building = f.Building.Trim();
            q = q.Where(a => a.Classroom!.Building == building);
        }
        if (!string.IsNullOrWhiteSpace(f.AlertType))
        {
            var type = f.AlertType.Trim();
            q = q.Where(a => a.AlertType == type);
        }
        if (f.Status is AlertStatus status) q = q.Where(a => a.Status == status);
        var (from, toExclusive) = ServiceHelpers.DateRange(f.StartDate, f.EndDate);
        if (from is DateTime fr) q = q.Where(a => a.ReceivedAtUtc >= fr);
        if (toExclusive is DateTime te) q = q.Where(a => a.ReceivedAtUtc < te);
        return q;
    }

    public static IQueryable<AlertRow> Project(ISafeClassDbContext db, IQueryable<EmergencyAlert> q) =>
        q.Select(a => new AlertRow
        {
            Id = a.Id,
            ClassroomId = a.ClassroomId,
            RoomCode = a.Classroom!.Code,
            RoomName = a.Classroom.Name,
            Building = a.Classroom.Building,
            Type = a.AlertType,
            Status = a.Status,
            ReceivedAtUtc = a.ReceivedAtUtc,
            AcknowledgedAtUtc = a.AcknowledgedAtUtc,
            AcknowledgedBy = db.Users.Where(u => u.Id == a.AcknowledgedByUserId).Select(u => u.FullName).FirstOrDefault(),
            ResolvedAtUtc = a.ResolvedAtUtc,
            ResolvedBy = db.Users.Where(u => u.Id == a.ResolvedByUserId).Select(u => u.FullName).FirstOrDefault(),
            ResolutionNotes = a.ResolutionNotes,
            Source = a.Source,
            DeviceId = a.DeviceId
        });
}
