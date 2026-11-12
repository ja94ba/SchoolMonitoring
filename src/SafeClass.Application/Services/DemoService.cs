using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeClass.Application.Common;
using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Options;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Services;

/// <summary>
/// Simulate Alert / Reset Demo Data. Both are refused unless demo mode is explicitly enabled AND the caller's
/// school is flagged IsDemo. Reset removes only alerts created by the simulator (source "demo"), never seeded
/// history and never anything in a real school.
/// </summary>
public sealed class DemoService(
    ISafeClassDbContext db,
    ICurrentUser user,
    IAlertService alerts,
    IMonitoringNotifier notifier,
    TimeProvider time,
    IOptions<SafeClassOptions> options,
    ILogger<DemoService> log) : IDemoService
{
    public async Task<AlertDto> SimulateAlertAsync(CancellationToken ct = default)
    {
        var schoolId = await RequireDemoSchoolAsync(ct);
        var pool = await db.Classrooms.AsNoTracking()
            .Where(c => c.SchoolId == schoolId && c.IsActive && c.CurrentStatus == ClassroomStatus.Normal)
            .Select(c => c.Id).ToListAsync(ct);
        if (pool.Count == 0)
            throw new ConflictException("No normal classrooms are available. Resolve alerts or reset the demo data.");

        var types = options.Value.AllowedAlertTypes;
        var request = new CreateAlertRequest
        {
            ClassroomId = pool[Random.Shared.Next(pool.Count)],
            AlertType = types[Random.Shared.Next(types.Length)],
            Notes = "Simulated alert (demo mode)"
        };
        return await alerts.CreateAsync(request, EventSources.Demo, ct);
    }

    public async Task<int> ResetAsync(CancellationToken ct = default)
    {
        var schoolId = await RequireDemoSchoolAsync(ct);
        var userId = user.RequireUser();

        var alertIds = await db.EmergencyAlerts.Where(a => a.SchoolId == schoolId && a.Source == EventSources.Demo)
            .Select(a => a.Id).ToListAsync(ct);
        if (alertIds.Count == 0) return 0;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.AlertEventHistory.Where(e => alertIds.Contains(e.AlertId)).ExecuteDeleteAsync(ct);
        await db.ActivityLogs.Where(a => a.AlertId != null && alertIds.Contains(a.AlertId.Value)).ExecuteDeleteAsync(ct);
        await db.ClassroomStatusHistory.Where(h => h.RelatedAlertId != null && alertIds.Contains(h.RelatedAlertId.Value)).ExecuteDeleteAsync(ct);
        await db.EmergencyAlerts.Where(a => alertIds.Contains(a.Id)).ExecuteDeleteAsync(ct);

        // Re-derive each classroom's status from what is left.
        var now = time.GetUtcNow().UtcDateTime;
        var rooms = await db.Classrooms.Where(c => c.SchoolId == schoolId && c.IsActive).ToListAsync(ct);
        var devices = await db.Devices.Where(d => d.SchoolId == schoolId && d.IsActive).ToListAsync(ct);
        var changed = new List<(Classroom Room, Device? Device)>();
        foreach (var room in rooms)
        {
            var device = devices.Where(d => d.ClassroomId == room.Id).OrderBy(d => d.RegisteredAtUtc).FirstOrDefault();
            var hasUnresolved = await ServiceHelpers.HasUnresolvedAlertsAsync(db, room.Id, null, ct);
            var status = StatusEngine.Compute(hasUnresolved, device);
            if (room.CurrentStatus != status)
            {
                room.CurrentStatus = status;
                room.LastStatusUpdateUtc = now;
                room.UpdatedAtUtc = now;
                changed.Add((room, device));
            }
        }
        db.AuditLogs.Add(new AuditLog
        {
            SchoolId = schoolId, UserId = userId, Action = "demo.reset", EntityType = nameof(School), EntityId = schoolId.ToString(),
            TimestampUtc = now, Details = $"Removed {alertIds.Count} simulated alert(s)"
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        foreach (var (room, device) in changed)
        {
            var (r, d) = (room, device);
            await ServiceHelpers.SafeNotifyAsync(() => notifier.ClassroomStatusChangedAsync(schoolId, r.ToDto(d), ct), log);
        }
        return alertIds.Count;
    }

    private async Task<Guid> RequireDemoSchoolAsync(CancellationToken ct)
    {
        if (!options.Value.DemoModeEnabled) throw new NotFoundException("Demo mode is not enabled.");
        var schoolId = user.RequireSchool();
        var isDemo = await db.Schools.AsNoTracking().Where(s => s.Id == schoolId).Select(s => s.IsDemo).FirstOrDefaultAsync(ct);
        if (!isDemo) throw new ForbiddenException("Demo actions are only available for demo schools.");
        return schoolId;
    }
}

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers the application services. Configure SafeClassOptions in the host (Program.cs).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ISchoolService, SchoolService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IClassroomService, ClassroomService>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IDemoService, DemoService>();
        return services;
    }
}
