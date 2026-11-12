using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeClass.Application.Common;
using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Options;
using SafeClass.Application.Validation;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Services;

/// <summary>
/// Emergency alert lifecycle. Every state change is a single SaveChanges (one SQLite transaction) that writes the
/// alert, its event history, the classroom status transition, the activity log and (for staff actions) the audit log.
/// Real-time notifications are published only after that commit succeeds.
/// </summary>
public sealed class AlertService(
    ISafeClassDbContext db,
    ICurrentUser user,
    IMonitoringNotifier notifier,
    TimeProvider time,
    IOptions<SafeClassOptions> options,
    ILogger<AlertService> log) : IAlertService
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<PagedResult<AlertDto>> ListAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var (page, size) = Paging.Normalize(filter.Page, filter.PageSize);
        var q = AlertQueries.ApplyFilter(db.EmergencyAlerts.AsNoTracking(), filter, schoolId);
        var total = await q.CountAsync(ct);
        var rows = await AlertQueries.Project(db, q)
            .OrderByDescending(r => r.ReceivedAtUtc)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);
        return new PagedResult<AlertDto>
        {
            Items = rows.Select(r => r.ToDto()).ToList(), Page = page, PageSize = size, TotalCount = total
        };
    }

    public async Task<AlertDto> GetAsync(Guid id, CancellationToken ct = default) =>
        await LoadDtoAsync(id, user.RequireSchool(), ct);

    public async Task<AlertDto> CreateAsync(CreateAlertRequest request, string source = EventSources.User, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var type = Validators.ValidateAlertType(request.AlertType, options.Value.AllowedAlertTypes);
        var errors = new Dictionary<string, string[]>();
        var notes = Validators.Optional(errors, "notes", request.Notes, 500);
        Validators.Throw(errors);

        var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == request.ClassroomId && c.SchoolId == schoolId && c.IsActive, ct)
                   ?? throw new NotFoundException("Classroom not found.");
        var device = await ServiceHelpers.ActiveDeviceAsync(db, room.Id, ct);

        var result = await PersistNewAlertAsync(room, null, type, source, userId, null, notes, ct);
        await PublishCreatedAsync(result, room, device, ct);
        return await LoadDtoAsync(result.Alert.Id, schoolId, ct);
    }

    public async Task<DeviceAlertResult> CreateFromDeviceAsync(Guid deviceId, DeviceAlertRequest request, CancellationToken ct = default)
    {
        // The API layer authenticates the device key; this is a second, independent check.
        if (user.IsDevice && user.DeviceId != deviceId)
            throw new ForbiddenException("A device may only submit alerts for itself.");

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                     ?? throw new NotFoundException("Device not found.");
        if (!device.IsActive || device.CredentialRevokedAtUtc.HasValue)
            throw new ForbiddenException("This device is not active.");
        if (user.SchoolId.HasValue && user.SchoolId != device.SchoolId)
            throw new ForbiddenException("You do not have access to that device.");
        if (!string.IsNullOrWhiteSpace(request.DeviceIdentifier) &&
            !string.Equals(request.DeviceIdentifier.Trim(), device.DeviceIdentifier, StringComparison.Ordinal))
            throw new AppValidationException("deviceIdentifier", "deviceIdentifier does not match the authenticated device.");

        var type = Validators.ValidateAlertType(request.AlertType, options.Value.AllowedAlertTypes);
        var eventId = Validators.ValidateEventId(request.EventId);

        var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == device.ClassroomId && c.IsActive, ct)
                   ?? throw new NotFoundException("The device's classroom was not found.");

        var result = await PersistNewAlertAsync(room, device, type, EventSources.Device, null, eventId, null, ct);
        if (!result.Duplicate) await PublishCreatedAsync(result, room, device, ct);

        return new DeviceAlertResult
        {
            Alert = await LoadDtoAsync(result.Alert.Id, device.SchoolId, ct),
            Duplicate = result.Duplicate
        };
    }

    public async Task<AlertDto> AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var alert = await db.EmergencyAlerts.FirstOrDefaultAsync(a => a.Id == id && a.SchoolId == schoolId, ct)
                    ?? throw new NotFoundException("Alert not found.");

        if (alert.Status == AlertStatus.Acknowledged) throw new ConflictException("This alert has already been acknowledged.");
        if (alert.Status == AlertStatus.Resolved) throw new ConflictException("This alert is already resolved.");

        var now = Now;
        var room = await db.Classrooms.FirstAsync(c => c.Id == alert.ClassroomId, ct);
        var actor = await ActorNameAsync(userId, ct);

        alert.Status = AlertStatus.Acknowledged;
        alert.AcknowledgedAtUtc = now;
        alert.AcknowledgedByUserId = userId;

        db.AlertEventHistory.Add(new AlertEventHistory
        {
            AlertId = alert.Id, EventType = AlertEventTypes.Acknowledged,
            PreviousStatus = AlertStatus.Active, NewStatus = AlertStatus.Acknowledged,
            EventTimestampUtc = now, PerformedByUserId = userId, Source = EventSources.User
        });
        db.ActivityLogs.Add(new ActivityLog
        {
            SchoolId = schoolId, ClassroomId = room.Id, AlertId = alert.Id, EventType = ActivityEventTypes.AlertAcknowledged,
            Description = $"{room.Code} {alert.AlertType} alert acknowledged by {actor}",
            CreatedAtUtc = now, ActorUserId = userId, Source = EventSources.User
        });
        AddAudit(schoolId, userId, "alert.acknowledge", alert.Id, $"{room.Code} {alert.AlertType}", now);
        // Acknowledging never changes the classroom status: it stays Emergency until resolved.

        await SaveOrConflictAsync(ct);

        var dto = await LoadDtoAsync(alert.Id, schoolId, ct);
        await ServiceHelpers.SafeNotifyAsync(() => notifier.AlertAcknowledgedAsync(schoolId, dto, ct), log);
        return dto;
    }

    public async Task<AlertDto> ResolveAsync(Guid id, ResolveAlertRequest request, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var errors = new Dictionary<string, string[]>();
        var notes = Validators.Optional(errors, "notes", request.Notes, 500);
        Validators.Throw(errors);

        var alert = await db.EmergencyAlerts.FirstOrDefaultAsync(a => a.Id == id && a.SchoolId == schoolId, ct)
                    ?? throw new NotFoundException("Alert not found.");
        if (alert.Status == AlertStatus.Resolved) throw new ConflictException("This alert is already resolved.");

        var now = Now;
        var room = await db.Classrooms.FirstAsync(c => c.Id == alert.ClassroomId, ct);
        var device = await ServiceHelpers.ActiveDeviceAsync(db, room.Id, ct);
        var actor = await ActorNameAsync(userId, ct);

        // The UI lets a responder resolve an alert directly. Record that as an acknowledgement at the same
        // instant (with a note) so acknowledgement-time reports stay complete and honest about it.
        if (alert.Status == AlertStatus.Active)
        {
            alert.AcknowledgedAtUtc = now;
            alert.AcknowledgedByUserId = userId;
            db.AlertEventHistory.Add(new AlertEventHistory
            {
                AlertId = alert.Id, EventType = AlertEventTypes.Acknowledged,
                PreviousStatus = AlertStatus.Active, NewStatus = AlertStatus.Acknowledged,
                EventTimestampUtc = now, PerformedByUserId = userId, Source = EventSources.User,
                Notes = "Implicitly acknowledged when resolved"
            });
        }

        alert.Status = AlertStatus.Resolved;
        alert.ResolvedAtUtc = now;
        alert.ResolvedByUserId = userId;
        alert.ResolutionNotes = notes;

        db.AlertEventHistory.Add(new AlertEventHistory
        {
            AlertId = alert.Id, EventType = AlertEventTypes.Resolved,
            PreviousStatus = AlertStatus.Acknowledged, NewStatus = AlertStatus.Resolved,
            EventTimestampUtc = now, PerformedByUserId = userId, Source = EventSources.User, Notes = notes
        });

        // Back to Normal only if no other unresolved alert remains AND the device is online.
        var othersOpen = await ServiceHelpers.HasUnresolvedAlertsAsync(db, room.Id, alert.Id, ct);
        var newStatus = StatusEngine.Compute(othersOpen, device);
        var statusChanged = StatusEngine.Apply(db, room, newStatus, now, EventSources.User, userId, alert.Id,
            othersOpen ? "Alert resolved; other alerts remain open" : "Alert resolved");

        db.ActivityLogs.Add(new ActivityLog
        {
            SchoolId = schoolId, ClassroomId = room.Id, AlertId = alert.Id, EventType = ActivityEventTypes.AlertResolved,
            Description = $"{room.Code} {room.Name} alert resolved by {actor}",
            CreatedAtUtc = now, ActorUserId = userId, Source = EventSources.User
        });
        AddAudit(schoolId, userId, "alert.resolve", alert.Id, $"{room.Code} {alert.AlertType}", now);

        await SaveOrConflictAsync(ct);

        var dto = await LoadDtoAsync(alert.Id, schoolId, ct);
        await ServiceHelpers.SafeNotifyAsync(async () =>
        {
            await notifier.AlertResolvedAsync(schoolId, dto, ct);
            if (statusChanged) await notifier.ClassroomStatusChangedAsync(schoolId, room.ToDto(device), ct);
        }, log);
        return dto;
    }

    public async Task<IReadOnlyList<AlertEventDto>> GetHistoryAsync(Guid id, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var exists = await db.EmergencyAlerts.AnyAsync(a => a.Id == id && a.SchoolId == schoolId, ct);
        if (!exists) throw new NotFoundException("Alert not found.");

        var rows = await db.AlertEventHistory.AsNoTracking()
            .Where(e => e.AlertId == id)
            .OrderBy(e => e.EventTimestampUtc).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id, e.EventType, e.PreviousStatus, e.NewStatus, e.EventTimestampUtc, e.Source, e.Notes,
                PerformedBy = db.Users.Where(u => u.Id == e.PerformedByUserId).Select(u => u.FullName).FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows.Select(e => new AlertEventDto
        {
            Id = e.Id, EventType = e.EventType, PreviousStatus = e.PreviousStatus, NewStatus = e.NewStatus,
            Timestamp = e.EventTimestampUtc.ToUnixMs(), PerformedBy = e.PerformedBy, Source = e.Source, Notes = e.Notes
        }).ToList();
    }

    // ---------------------------------------------------------------- internals

    private sealed record NewAlertResult(EmergencyAlert Alert, bool Duplicate, bool StatusChanged, bool DeviceCameOnline);

    private async Task<NewAlertResult> PersistNewAlertAsync(
        Classroom room, Device? device, string type, string source, Guid? userId, string? eventId, string? notes, CancellationToken ct)
    {
        if (device is not null && eventId is not null)
        {
            var existing = await db.EmergencyAlerts.FirstOrDefaultAsync(a => a.DeviceId == device.Id && a.DeviceEventId == eventId, ct);
            if (existing is not null) return new NewAlertResult(existing, true, false, false);
        }

        var now = Now;
        var alert = new EmergencyAlert
        {
            Id = Guid.NewGuid(), SchoolId = room.SchoolId, ClassroomId = room.Id, DeviceId = device?.Id,
            AlertType = type, Status = AlertStatus.Active, ReceivedAtUtc = now, Source = source,
            DeviceEventId = eventId, CreatedAtUtc = now
        };
        db.EmergencyAlerts.Add(alert);
        db.AlertEventHistory.Add(new AlertEventHistory
        {
            AlertId = alert.Id, EventType = AlertEventTypes.Created, PreviousStatus = null, NewStatus = AlertStatus.Active,
            EventTimestampUtc = now, PerformedByUserId = userId, Source = source, Notes = notes
        });

        // An alert proves the device is alive: record the reconnection if it was marked offline.
        var cameOnline = false;
        if (device is not null && DeviceConnection.MarkOnline(db, device, now, "Alert received"))
        {
            cameOnline = true;
            db.ActivityLogs.Add(new ActivityLog
            {
                SchoolId = room.SchoolId, ClassroomId = room.Id, EventType = ActivityEventTypes.DeviceOnline,
                Description = $"{room.Code} {room.Name} is back online", CreatedAtUtc = now, Source = EventSources.Device
            });
        }

        // Emergency outranks Offline, so an alert always makes the classroom Emergency.
        var changed = StatusEngine.Apply(db, room, ClassroomStatus.Emergency, now, source, userId, alert.Id, $"{type} alert received");

        db.ActivityLogs.Add(new ActivityLog
        {
            SchoolId = room.SchoolId, ClassroomId = room.Id, AlertId = alert.Id, EventType = ActivityEventTypes.AlertCreated,
            Description = $"{room.Code} {room.Name} sent a {type} alert",
            CreatedAtUtc = now, ActorUserId = userId, Source = source
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (device is not null && eventId is not null && IsUniqueViolation(ex))
        {
            // A concurrent retry of the same device event won the race. Return its alert instead.
            db.ChangeTracker.Clear();
            var winner = await db.EmergencyAlerts.AsNoTracking()
                .FirstAsync(a => a.DeviceId == device.Id && a.DeviceEventId == eventId, ct);
            return new NewAlertResult(winner, true, false, false);
        }

        return new NewAlertResult(alert, false, changed, cameOnline);
    }

    private async Task PublishCreatedAsync(NewAlertResult result, Classroom room, Device? device, CancellationToken ct)
    {
        var dto = await LoadDtoAsync(result.Alert.Id, room.SchoolId, ct);
        await ServiceHelpers.SafeNotifyAsync(async () =>
        {
            await notifier.AlertCreatedAsync(room.SchoolId, dto, ct);
            if (result.StatusChanged) await notifier.ClassroomStatusChangedAsync(room.SchoolId, room.ToDto(device), ct);
            if (result.DeviceCameOnline && device is not null)
                await notifier.DeviceConnectionChangedAsync(room.SchoolId, device.ToDto(room.Code), ct);
        }, log);
    }

    private async Task<AlertDto> LoadDtoAsync(Guid id, Guid schoolId, CancellationToken ct)
    {
        var row = await AlertQueries.Project(db, db.EmergencyAlerts.AsNoTracking().Where(a => a.Id == id && a.SchoolId == schoolId))
            .FirstOrDefaultAsync(ct);
        return row?.ToDto() ?? throw new NotFoundException("Alert not found.");
    }

    private async Task<string> ActorNameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "a staff member";

    private void AddAudit(Guid schoolId, Guid userId, string action, Guid alertId, string details, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            SchoolId = schoolId, UserId = userId, Action = action, EntityType = nameof(EmergencyAlert),
            EntityId = alertId.ToString(), TimestampUtc = now, Details = details
        });

    private async Task SaveOrConflictAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new ConflictException("This alert was changed by someone else. Refresh and try again.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true;
}
