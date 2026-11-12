using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SafeClass.Application.Common;
using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Security;
using SafeClass.Application.Validation;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Services;

public sealed class DashboardService(ISafeClassDbContext db, ICurrentUser user) : IDashboardService
{
    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();

        var byStatus = await db.Classrooms.AsNoTracking()
            .Where(c => c.SchoolId == schoolId && c.IsActive)
            .GroupBy(c => c.CurrentStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Count(ClassroomStatus s) => byStatus.Where(x => x.Status == s).Select(x => x.Count).FirstOrDefault();

        var buildings = await db.Classrooms.AsNoTracking()
            .Where(c => c.SchoolId == schoolId && c.IsActive)
            .Select(c => c.Building).Distinct().CountAsync(ct);

        var alerts = await db.EmergencyAlerts.AsNoTracking()
            .Where(a => a.SchoolId == schoolId && a.Status != AlertStatus.Resolved)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var active = alerts.Where(x => x.Status == AlertStatus.Active).Select(x => x.Count).FirstOrDefault();
        var acknowledged = alerts.Where(x => x.Status == AlertStatus.Acknowledged).Select(x => x.Count).FirstOrDefault();

        return new DashboardSummaryDto
        {
            TotalClassrooms = byStatus.Sum(x => x.Count),
            NormalClassrooms = Count(ClassroomStatus.Normal),
            EmergencyClassrooms = Count(ClassroomStatus.Emergency),
            OfflineClassrooms = Count(ClassroomStatus.Offline),
            ActiveAlerts = active,
            AcknowledgedAlerts = acknowledged,
            OpenAlerts = active + acknowledged,
            Buildings = buildings
        };
    }

    public async Task<IReadOnlyList<ActivityDto>> GetActivityAsync(int limit, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        if (limit < 1) limit = 20;
        if (limit > 100) limit = 100;

        // User-facing messages come from persisted activity rows, not from in-memory state.
        var rows = await db.ActivityLogs.AsNoTracking()
            .Where(a => a.SchoolId == schoolId)
            .OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(a => new ActivityDto
        {
            Id = a.Id, Ts = a.CreatedAtUtc.ToUnixMs(), Kind = Mapping.ActivityKind(a.EventType), Text = a.Description,
            ClassroomId = a.ClassroomId, AlertId = a.AlertId
        }).ToList();
    }
}

public sealed class SchoolService(ISafeClassDbContext db, ICurrentUser user, TimeProvider time) : ISchoolService
{
    public async Task<SchoolDto> GetCurrentAsync(CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var school = await db.Schools.AsNoTracking().FirstOrDefaultAsync(s => s.Id == schoolId, ct)
                     ?? throw new NotFoundException("School not found.");
        return ToDto(school);
    }

    public async Task<SchoolDto> UpdateAsync(Guid id, UpdateSchoolRequest request, CancellationToken ct = default)
    {
        var schoolId = user.RequireSchool();
        var userId = user.RequireUser();
        if (id != schoolId) throw new ForbiddenException("You do not have access to that school.");
        var r = request.Validate();

        var school = await db.Schools.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("School not found.");
        var now = time.GetUtcNow().UtcDateTime;
        school.Name = r.Name;
        school.Address = r.Address;
        school.City = r.City;
        school.Province = r.Province;
        school.Country = r.Country;
        school.UpdatedAtUtc = now;
        db.AuditLogs.Add(new AuditLog
        {
            SchoolId = schoolId, UserId = userId, Action = "school.update", EntityType = nameof(School),
            EntityId = school.Id.ToString(), TimestampUtc = now, Details = "School details updated"
        });
        await db.SaveChangesAsync(ct);
        return ToDto(school);
    }

    private static SchoolDto ToDto(School s) => new()
    {
        Id = s.Id, SchoolCode = s.SchoolCode, Name = s.Name, Address = s.Address, City = s.City,
        Province = s.Province, Country = s.Country, IsDemo = s.IsDemo
    };
}

public sealed class UserService(ISafeClassDbContext db, ICurrentUser user, TimeProvider time) : IUserService
{
    public async Task<UserDto> GetMeAsync(CancellationToken ct = default)
    {
        var userId = user.RequireUser();
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { User = u, SchoolName = db.Schools.Where(s => s.Id == u.SchoolId).Select(s => s.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("User not found.");
        return new UserDto
        {
            Id = row.User.Id, SchoolId = row.User.SchoolId, FullName = row.User.FullName, Email = row.User.Email,
            Role = row.User.Role, SchoolName = row.SchoolName ?? ""
        };
    }

    public async Task<PreferencesDto> GetPreferencesAsync(CancellationToken ct = default)
    {
        var userId = user.RequireUser();
        var p = await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return p is null ? new PreferencesDto() : ToDto(p);
    }

    public async Task<PreferencesDto> UpdatePreferencesAsync(PreferencesDto request, CancellationToken ct = default)
    {
        var userId = user.RequireUser();
        var now = time.GetUtcNow().UtcDateTime;
        var p = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (p is null)
        {
            p = new NotificationPreference { Id = Guid.NewGuid(), UserId = userId };
            db.NotificationPreferences.Add(p);
        }
        p.InAppBannerEnabled = request.InAppBannerEnabled;
        p.AlertSoundEnabled = request.AlertSoundEnabled;
        p.PushNotificationsEnabled = request.PushNotificationsEnabled;
        p.SmsEnabled = request.SmsEnabled;
        p.DailyEmailSummaryEnabled = request.DailyEmailSummaryEnabled;
        p.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    private static PreferencesDto ToDto(NotificationPreference p) => new()
    {
        InAppBannerEnabled = p.InAppBannerEnabled, AlertSoundEnabled = p.AlertSoundEnabled,
        PushNotificationsEnabled = p.PushNotificationsEnabled, SmsEnabled = p.SmsEnabled,
        DailyEmailSummaryEnabled = p.DailyEmailSummaryEnabled
    };
}

public sealed class AuthService(ISafeClassDbContext db, ITokenService tokens, TimeProvider time, ILogger<AuthService> log) : IAuthService
{
    // Verified against when the email is unknown so response time does not reveal which emails exist.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("not-a-real-password"));

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        var password = request.Password ?? "";
        if (email.Length == 0 || password.Length == 0 || email.Length > 254 || password.Length > 200)
            throw new UnauthorizedAppException("Invalid email or password.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var passwordOk = PasswordHasher.Verify(password, user?.PasswordHash ?? DummyHash.Value);
        var school = user is null ? null : await db.Schools.AsNoTracking().FirstOrDefaultAsync(s => s.Id == user.SchoolId, ct);

        if (user is null || !passwordOk || !user.IsActive || school is null || !school.IsActive)
        {
            log.LogWarning("Failed sign-in attempt");
            throw new UnauthorizedAppException("Invalid email or password.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        db.AuditLogs.Add(new AuditLog
        {
            SchoolId = user.SchoolId, UserId = user.Id, Action = "auth.login", EntityType = nameof(User),
            EntityId = user.Id.ToString(), TimestampUtc = now, Details = "Signed in"
        });
        await db.SaveChangesAsync(ct);

        var (token, expires) = tokens.CreateToken(user);
        return new LoginResponse
        {
            Token = token, ExpiresAt = expires.ToUnixMs(),
            User = new UserDto
            {
                Id = user.Id, SchoolId = user.SchoolId, FullName = user.FullName, Email = user.Email,
                Role = user.Role, SchoolName = school.Name
            }
        };
    }
}
