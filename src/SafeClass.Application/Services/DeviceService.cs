using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeClass.Application.Common;
using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Options;
using SafeClass.Application.Security;
using SafeClass.Application.Validation;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Services;

public sealed class DeviceService(
    ISafeClassDbContext db,
    ICurrentUser user,
    IMonitoringNotifier notifier,
    TimeProvider time,
    IOptions<SafeClassOptions> options,
    ILogger<DeviceService> log) : IDeviceService
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var rows = await db.Devices.AsNoTracking()
            .Where(d => d.SchoolId == schoolId)
            .OrderBy(d => d.DeviceIdentifier)
            .Select(d => new { Device = d, Code = d.Classroom!.Code })
            .ToListAsync(ct);
        return rows.Select(r => r.Device.ToDto(r.Code)).ToList();
    }

    public async Task<DeviceDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var row = await db.Devices.AsNoTracking()
            .Where(d => d.Id == id && d.SchoolId == schoolId)
            .Select(d => new { Device = d, Code = d.Classroom!.Code })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Device not found.");
        return row.Device.ToDto(row.Code);
    }

    public async Task<DeviceCredentialResponse> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var r = request.Validate();

        var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == r.ClassroomId && c.SchoolId == schoolId && c.IsActive, ct)
                   ?? throw new NotFoundException("Classroom not found.");
        if (await db.Devices.AnyAsync(d => d.DeviceIdentifier == r.DeviceIdentifier, ct))
            throw new ConflictException($"A device with identifier '{r.DeviceIdentifier}' is already registered.");
        if (await db.Devices.AnyAsync(d => d.ClassroomId == room.Id && d.IsActive, ct))
            throw new ConflictException("This classroom already has an active device. Revoke it before registering another.");

        var now = Now;
        var key = DeviceCredentials.GenerateKey();
        var device = new Device
        {
            Id = Guid.NewGuid(), SchoolId = schoolId, ClassroomId = room.Id, DeviceIdentifier = r.DeviceIdentifier,
            DeviceName = r.DeviceName ?? r.DeviceIdentifier, DeviceType = r.DeviceType ?? "ESP32",
            FirmwareVersion = r.FirmwareVersion, SignalStrength = 0, IsOnline = false, LastHeartbeatUtc = null,
            RegisteredAtUtc = now, LastUpdatedUtc = now, IsActive = true, ApiKeyHash = DeviceCredentials.Hash(key)
        };
        db.Devices.Add(device);

        var hasUnresolved = await ServiceHelpers.HasUnresolvedAlertsAsync(db, room.Id, null, ct);
        var changed = StatusEngine.Apply(db, room, StatusEngine.Compute(hasUnresolved, device), now, EventSources.User, userId, null,
            "Device registered (waiting for first heartbeat)");

        db.ActivityLogs.Add(new ActivityLog
        {
            SchoolId = schoolId, ClassroomId = room.Id, EventType = ActivityEventTypes.DeviceRegistered,
            Description = $"Device {device.DeviceIdentifier} registered for {room.Code} {room.Name}",
            CreatedAtUtc = now, ActorUserId = userId, Source = EventSources.User
        });
        Audit(schoolId, userId, "device.register", device.Id, device.DeviceIdentifier, now);
        await db.SaveChangesAsync(ct);

        if (changed)
            await ServiceHelpers.SafeNotifyAsync(() => notifier.ClassroomStatusChangedAsync(schoolId, room.ToDto(device), ct), log);

        return new DeviceCredentialResponse { Device = device.ToDto(room.Code), ApiKey = key };
    }

    public async Task<DeviceCredentialResponse> RotateKeyAsync(Guid id, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var device = await db.Devices.Include(d => d.Classroom).FirstOrDefaultAsync(d => d.Id == id && d.SchoolId == schoolId, ct)
                     ?? throw new NotFoundException("Device not found.");
        if (!device.IsActive || device.CredentialRevokedAtUtc.HasValue)
            throw new ConflictException("This device has been revoked. Register a new device instead.");

        var key = DeviceCredentials.GenerateKey();
        var now = Now;
        device.ApiKeyHash = DeviceCredentials.Hash(key);
        device.LastUpdatedUtc = now;
        Audit(schoolId, userId, "device.rotate-key", device.Id, device.DeviceIdentifier, now);
        await db.SaveChangesAsync(ct);
        return new DeviceCredentialResponse { Device = device.ToDto(device.Classroom?.Code), ApiKey = key };
    }

    public async Task<DeviceDto> RevokeCredentialsAsync(Guid id, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var device = await db.Devices.Include(d => d.Classroom).FirstOrDefaultAsync(d => d.Id == id && d.SchoolId == schoolId, ct)
                     ?? throw new NotFoundException("Device not found.");
        var room = device.Classroom!;
        var now = Now;

        device.CredentialRevokedAtUtc ??= now;
        device.ApiKeyHash = "";
        device.IsActive = false;
        var wentOffline = DeviceConnection.MarkOffline(db, device, now, "Credentials revoked");

        var hasUnresolved = await ServiceHelpers.HasUnresolvedAlertsAsync(db, room.Id, null, ct);
        var replacement = await ServiceHelpers.ActiveDeviceAsync(db, room.Id, ct);
        var changed = StatusEngine.Apply(db, room, StatusEngine.Compute(hasUnresolved, replacement), now, EventSources.User, userId, null,
            "Device credentials revoked");
        Audit(schoolId, userId, "device.revoke", device.Id, device.DeviceIdentifier, now);
        await db.SaveChangesAsync(ct);

        await ServiceHelpers.SafeNotifyAsync(async () =>
        {
            if (wentOffline) await notifier.DeviceConnectionChangedAsync(schoolId, device.ToDto(room.Code), ct);
            if (changed) await notifier.ClassroomStatusChangedAsync(schoolId, room.ToDto(replacement), ct);
        }, log);
        return device.ToDto(room.Code);
    }

    public async Task<DeviceHistoryDto> GetHistoryAsync(Guid id, DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        if (!await db.Devices.AnyAsync(d => d.Id == id && d.SchoolId == schoolId, ct))
            throw new NotFoundException("Device not found.");
        (page, pageSize) = Paging.Normalize(page, pageSize);
        var (f, t) = ServiceHelpers.DateRange(from, to);

        var hq = db.DeviceHeartbeatHistory.AsNoTracking().Where(h => h.DeviceId == id);
        var cq = db.DeviceConnectionHistory.AsNoTracking().Where(h => h.DeviceId == id);
        if (f is DateTime fr) { hq = hq.Where(h => h.ReceivedAtUtc >= fr); cq = cq.Where(h => h.ChangedAtUtc >= fr); }
        if (t is DateTime te) { hq = hq.Where(h => h.ReceivedAtUtc < te); cq = cq.Where(h => h.ChangedAtUtc < te); }

        var total = await hq.CountAsync(ct);
        var beats = await hq.OrderByDescending(h => h.ReceivedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var events = await cq.OrderByDescending(h => h.ChangedAtUtc).Take(200).ToListAsync(ct);

        return new DeviceHistoryDto
        {
            Heartbeats = new PagedResult<HeartbeatHistoryDto>
            {
                Items = beats.Select(h => new HeartbeatHistoryDto
                {
                    Id = h.Id, ReceivedAt = h.ReceivedAtUtc.ToUnixMs(), SignalStrength = h.SignalStrength,
                    ReportedStatus = h.ReportedStatus, FirmwareVersion = h.FirmwareVersion, IsOnline = h.IsOnline
                }).ToList(),
                Page = page, PageSize = pageSize, TotalCount = total
            },
            ConnectionEvents = events.Select(e => new ConnectionEventDto
            {
                Id = e.Id, IsOnline = e.IsOnline, ChangedAt = e.ChangedAtUtc.ToUnixMs(), Reason = e.Reason
            }).ToList()
        };
    }

    public async Task<DeviceDto> RecordHeartbeatAsync(Guid deviceId, HeartbeatRequest request, CancellationToken ct = default)
    {
        var r = request.Validate();
        var device = await db.Devices.Include(d => d.Classroom).FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                     ?? throw new NotFoundException("Device not found.");
        if (!device.IsActive || device.CredentialRevokedAtUtc.HasValue)
            throw new ForbiddenException("This device is not active.");

        var room = device.Classroom!;
        var now = Now;
        device.SignalStrength = r.SignalStrength;
        if (r.FirmwareVersion is not null) device.FirmwareVersion = r.FirmwareVersion;

        var cameOnline = DeviceConnection.MarkOnline(db, device, now, "Heartbeat received");
        var statusChanged = false;
        if (cameOnline)
        {
            var hasUnresolved = await ServiceHelpers.HasUnresolvedAlertsAsync(db, room.Id, null, ct);
            statusChanged = StatusEngine.Apply(db, room, StatusEngine.Compute(hasUnresolved, device), now, EventSources.Device, null, null,
                "Device reconnected");
            db.ActivityLogs.Add(new ActivityLog
            {
                SchoolId = device.SchoolId, ClassroomId = room.Id, EventType = ActivityEventTypes.DeviceOnline,
                Description = $"{room.Code} {room.Name} is back online", CreatedAtUtc = now, Source = EventSources.Device
            });
        }

        // Keep the history table small: one row per device per interval, plus every state change.
        var lastStored = await db.DeviceHeartbeatHistory
            .Where(h => h.DeviceId == device.Id)
            .OrderByDescending(h => h.ReceivedAtUtc)
            .Select(h => (DateTime?)h.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (cameOnline || lastStored is null || (now - lastStored.Value).TotalSeconds >= options.Value.HeartbeatHistoryMinIntervalSeconds)
        {
            db.DeviceHeartbeatHistory.Add(new DeviceHeartbeatHistory
            {
                DeviceId = device.Id, ReceivedAtUtc = now, SignalStrength = r.SignalStrength,
                ReportedStatus = r.ReportedStatus, FirmwareVersion = device.FirmwareVersion, IsOnline = true
            });
        }

        await db.SaveChangesAsync(ct);

        var dto = device.ToDto(room.Code);
        if (cameOnline)
        {
            await ServiceHelpers.SafeNotifyAsync(async () =>
            {
                await notifier.DeviceConnectionChangedAsync(device.SchoolId, dto, ct);
                if (statusChanged) await notifier.ClassroomStatusChangedAsync(device.SchoolId, room.ToDto(device), ct);
            }, log);
        }
        return dto;
    }

    public async Task<int> DetectOfflineDevicesAsync(CancellationToken ct = default)
    {
        var now = Now;
        var cutoff = now.AddSeconds(-options.Value.HeartbeatOfflineThresholdSeconds);

        // Only devices that are currently marked online can start a new outage, so a device that stays
        // silent produces exactly one offline transition no matter how many checks run.
        var stale = await db.Devices.Include(d => d.Classroom)
            .Where(d => d.IsActive && d.IsOnline && (d.LastHeartbeatUtc ?? d.RegisteredAtUtc) < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;

        var changedRooms = new List<(Guid SchoolId, Classroom Room, Device Device)>();
        foreach (var device in stale)
        {
            var room = device.Classroom!;
            DeviceConnection.MarkOffline(db, device, now, "No heartbeat received");

            // The emergency condition is independent of connectivity: an open alert keeps the room in Emergency.
            var hasUnresolved = await ServiceHelpers.HasUnresolvedAlertsAsync(db, room.Id, null, ct);
            if (StatusEngine.Apply(db, room, StatusEngine.Compute(hasUnresolved, device), now, EventSources.System, null, null,
                    "Device stopped sending heartbeats"))
                changedRooms.Add((device.SchoolId, room, device));

            db.ActivityLogs.Add(new ActivityLog
            {
                SchoolId = device.SchoolId, ClassroomId = room.Id, EventType = ActivityEventTypes.DeviceOffline,
                Description = $"{room.Code} {room.Name} went offline", CreatedAtUtc = now, Source = EventSources.System
            });
        }

        await db.SaveChangesAsync(ct);

        foreach (var device in stale)
        {
            var d = device;
            await ServiceHelpers.SafeNotifyAsync(() => notifier.DeviceConnectionChangedAsync(d.SchoolId, d.ToDto(d.Classroom?.Code), ct), log);
        }
        foreach (var (schoolId, room, device) in changedRooms)
        {
            var (s, r, d) = (schoolId, room, device);
            await ServiceHelpers.SafeNotifyAsync(() => notifier.ClassroomStatusChangedAsync(s, r.ToDto(d), ct), log);
        }
        log.LogInformation("Marked {Count} device(s) offline after missed heartbeats", stale.Count);
        return stale.Count;
    }

    public async Task<int> PurgeOldHeartbeatsAsync(CancellationToken ct = default)
    {
        var days = options.Value.HeartbeatHistoryRetentionDays;
        if (days <= 0) return 0;
        var cutoff = Now.AddDays(-days);
        // Only heartbeat observations are purged. Alerts, status changes and outages are never deleted.
        var deleted = await db.DeviceHeartbeatHistory.Where(h => h.ReceivedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) log.LogInformation("Purged {Count} heartbeat history rows older than {Days} days", deleted, days);
        return deleted;
    }

    private void Audit(Guid schoolId, Guid userId, string action, Guid id, string details, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            SchoolId = schoolId, UserId = userId, Action = action, EntityType = nameof(Device),
            EntityId = id.ToString(), TimestampUtc = now, Details = details // never includes the API key
        });
}
