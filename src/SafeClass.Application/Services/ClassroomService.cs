using Microsoft.EntityFrameworkCore;
using SafeClass.Application.Common;
using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Validation;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Services;

public sealed class ClassroomService(ISafeClassDbContext db, ICurrentUser user, TimeProvider time) : IClassroomService
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<PagedResult<ClassroomDto>> ListAsync(ClassroomQuery query, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(query.SchoolId);
        var (page, size) = Paging.Normalize(query.Page, query.PageSize);

        var q = db.Classrooms.AsNoTracking().Where(c => c.SchoolId == schoolId);
        if (!query.IncludeInactive) q = q.Where(c => c.IsActive);
        if (query.Status is ClassroomStatus status) q = q.Where(c => c.CurrentStatus == status);
        if (!string.IsNullOrWhiteSpace(query.Building))
        {
            var building = query.Building.Trim();
            q = q.Where(c => c.Building == building);
        }
        if (!string.IsNullOrWhiteSpace(query.Floor))
        {
            var floor = query.Floor.Trim();
            q = q.Where(c => c.Floor == floor);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = ServiceHelpers.LikePattern(query.Search.Trim());
            q = q.Where(c => EF.Functions.Like(c.Code, pattern, "\\") || EF.Functions.Like(c.Name, pattern, "\\")
                          || EF.Functions.Like(c.Building, pattern, "\\") || EF.Functions.Like(c.Adviser, pattern, "\\"));
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(c => c.Code)
            .Skip((page - 1) * size).Take(size)
            .Select(c => new { Room = c, Device = c.Devices.Where(d => d.IsActive).OrderBy(d => d.RegisteredAtUtc).FirstOrDefault() })
            .ToListAsync(ct);

        return new PagedResult<ClassroomDto>
        {
            Items = rows.Select(r => r.Room.ToDto(r.Device)).ToList(), Page = page, PageSize = size, TotalCount = total
        };
    }

    public async Task<ClassroomDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var row = await db.Classrooms.AsNoTracking()
            .Where(c => c.Id == id && c.SchoolId == schoolId)
            .Select(c => new { Room = c, Device = c.Devices.Where(d => d.IsActive).OrderBy(d => d.RegisteredAtUtc).FirstOrDefault() })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Classroom not found.");
        return row.Room.ToDto(row.Device);
    }

    public async Task<ClassroomDto> CreateAsync(ClassroomRequest request, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var r = request.Validate();

        if (await db.Classrooms.AnyAsync(c => c.SchoolId == schoolId && c.Code == r.Code, ct))
            throw new ConflictException($"A classroom with code '{r.Code}' already exists in this school.");

        var now = Now;
        // A classroom without a device is not reporting, so it starts as Offline (see StatusEngine).
        var room = new Classroom
        {
            Id = Guid.NewGuid(), SchoolId = schoolId, Code = r.Code, Name = r.Name, Building = r.Building, Floor = r.Floor,
            Adviser = r.Adviser, CurrentStatus = ClassroomStatus.Offline, LastStatusUpdateUtc = now,
            CreatedAtUtc = now, UpdatedAtUtc = now, IsActive = true
        };
        db.Classrooms.Add(room);
        db.ClassroomStatusHistory.Add(new ClassroomStatusHistory
        {
            ClassroomId = room.Id, PreviousStatus = null, NewStatus = ClassroomStatus.Offline, ChangedAtUtc = now,
            TriggerSource = EventSources.User, TriggeredByUserId = userId, Reason = "Classroom created"
        });
        db.ActivityLogs.Add(new ActivityLog
        {
            SchoolId = schoolId, ClassroomId = room.Id, EventType = ActivityEventTypes.ClassroomCreated,
            Description = $"{room.Code} {room.Name} was added", CreatedAtUtc = now, ActorUserId = userId, Source = EventSources.User
        });
        Audit(schoolId, userId, "classroom.create", room.Id, room.Code, now);
        await db.SaveChangesAsync(ct);
        return room.ToDto(null);
    }

    public async Task<ClassroomDto> UpdateAsync(Guid id, ClassroomRequest request, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var r = request.Validate();

        var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == id && c.SchoolId == schoolId, ct)
                   ?? throw new NotFoundException("Classroom not found.");

        if (!string.Equals(room.Code, r.Code, StringComparison.Ordinal) &&
            await db.Classrooms.AnyAsync(c => c.SchoolId == schoolId && c.Code == r.Code && c.Id != id, ct))
            throw new ConflictException($"A classroom with code '{r.Code}' already exists in this school.");

        var now = Now;
        room.Code = r.Code; room.Name = r.Name; room.Building = r.Building; room.Floor = r.Floor; room.Adviser = r.Adviser;
        room.UpdatedAtUtc = now;
        Audit(schoolId, userId, "classroom.update", room.Id, room.Code, now);
        await db.SaveChangesAsync(ct);

        var device = await ServiceHelpers.ActiveDeviceAsync(db, room.Id, ct);
        return room.ToDto(device);
    }

    /// <summary>
    /// Soft delete: the classroom and its devices are deactivated but every alert, status change and heartbeat
    /// row stays in the database so history can still be reported on.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == id && c.SchoolId == schoolId, ct)
                   ?? throw new NotFoundException("Classroom not found.");
        if (!room.IsActive) return;

        if (await ServiceHelpers.HasUnresolvedAlertsAsync(db, room.Id, null, ct))
            throw new ConflictException("Resolve the open alerts for this classroom before removing it.");

        var now = Now;
        room.IsActive = false;
        room.UpdatedAtUtc = now;
        var devices = await db.Devices.Where(d => d.ClassroomId == room.Id && d.IsActive).ToListAsync(ct);
        foreach (var d in devices)
        {
            DeviceConnection.MarkOffline(db, d, now, "Classroom removed");
            d.IsActive = false;
        }
        Audit(schoolId, userId, "classroom.delete", room.Id, room.Code, now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<ClassroomHistoryDto>> GetHistoryAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        await EnsureExistsAsync(id, schoolId, ct);
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var q = db.ClassroomStatusHistory.AsNoTracking().Where(h => h.ClassroomId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(h => new
            {
                h.Id, h.PreviousStatus, h.NewStatus, h.ChangedAtUtc, h.TriggerSource, h.RelatedAlertId, h.Reason,
                TriggeredBy = db.Users.Where(u => u.Id == h.TriggeredByUserId).Select(u => u.FullName).FirstOrDefault()
            })
            .ToListAsync(ct);

        return new PagedResult<ClassroomHistoryDto>
        {
            Items = rows.Select(h => new ClassroomHistoryDto
            {
                Id = h.Id, PreviousStatus = h.PreviousStatus, NewStatus = h.NewStatus, ChangedAt = h.ChangedAtUtc.ToUnixMs(),
                TriggerSource = h.TriggerSource, TriggeredBy = h.TriggeredBy, RelatedAlertId = h.RelatedAlertId, Reason = h.Reason
            }).ToList(),
            Page = page, PageSize = pageSize, TotalCount = total
        };
    }

    public async Task<PagedResult<AlertDto>> GetAlertsAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        await EnsureExistsAsync(id, schoolId, ct);
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var q = db.EmergencyAlerts.AsNoTracking().Where(a => a.ClassroomId == id && a.SchoolId == schoolId);
        var total = await q.CountAsync(ct);
        var rows = await AlertQueries.Project(db, q).OrderByDescending(r => r.ReceivedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AlertDto>
        {
            Items = rows.Select(r => r.ToDto()).ToList(), Page = page, PageSize = pageSize, TotalCount = total
        };
    }

    private async Task EnsureExistsAsync(Guid id, Guid schoolId, CancellationToken ct)
    {
        if (!await db.Classrooms.AnyAsync(c => c.Id == id && c.SchoolId == schoolId, ct))
            throw new NotFoundException("Classroom not found.");
    }

    private void Audit(Guid schoolId, Guid userId, string action, Guid id, string details, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            SchoolId = schoolId, UserId = userId, Action = action, EntityType = nameof(Classroom),
            EntityId = id.ToString(), TimestampUtc = now, Details = details
        });
}
