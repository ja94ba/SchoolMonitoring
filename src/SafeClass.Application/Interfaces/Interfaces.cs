using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SafeClass.Application.DTOs;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Interfaces;

/// <summary>The slice of the DbContext the application services need. Implemented by SafeClass.Infrastructure.</summary>
public interface ISafeClassDbContext
{
    DbSet<School> Schools { get; }
    DbSet<User> Users { get; }
    DbSet<Classroom> Classrooms { get; }
    DbSet<Device> Devices { get; }
    DbSet<EmergencyAlert> EmergencyAlerts { get; }
    DbSet<ClassroomStatusHistory> ClassroomStatusHistory { get; }
    DbSet<DeviceHeartbeatHistory> DeviceHeartbeatHistory { get; }
    DbSet<DeviceConnectionHistory> DeviceConnectionHistory { get; }
    DbSet<AlertEventHistory> AlertEventHistory { get; }
    DbSet<ActivityLog> ActivityLogs { get; }
    DbSet<NotificationPreference> NotificationPreferences { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The caller of the current request (a signed-in user or an authenticated device).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    Guid? SchoolId { get; }
    UserRole? Role { get; }
    bool IsDevice { get; }
    Guid? DeviceId { get; }
}

public static class CurrentUserExtensions
{
    public static Guid RequireSchool(this ICurrentUser user) =>
        user.SchoolId ?? throw new Exceptions.ForbiddenException("No school is associated with this caller.");

    public static Guid RequireUser(this ICurrentUser user) =>
        user.UserId ?? throw new Exceptions.ForbiddenException("This action requires a signed-in user.");

    /// <summary>Optional schoolId filters may only name the caller's own school.</summary>
    public static Guid ResolveSchool(this ICurrentUser user, Guid? requested)
    {
        var own = user.RequireSchool();
        if (requested.HasValue && requested.Value != own)
            throw new Exceptions.ForbiddenException("You do not have access to that school.");
        return own;
    }
}

/// <summary>Pushes change notifications to connected clients. Called only after the database commit succeeded.</summary>
public interface IMonitoringNotifier
{
    Task ClassroomStatusChangedAsync(Guid schoolId, ClassroomDto classroom, CancellationToken ct = default);
    Task AlertCreatedAsync(Guid schoolId, AlertDto alert, CancellationToken ct = default);
    Task AlertAcknowledgedAsync(Guid schoolId, AlertDto alert, CancellationToken ct = default);
    Task AlertResolvedAsync(Guid schoolId, AlertDto alert, CancellationToken ct = default);
    Task DeviceConnectionChangedAsync(Guid schoolId, DeviceDto device, CancellationToken ct = default);
}

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) CreateToken(User user);
}

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
}

public interface ISchoolService
{
    Task<SchoolDto> GetCurrentAsync(CancellationToken ct = default);
    Task<SchoolDto> UpdateAsync(Guid id, UpdateSchoolRequest request, CancellationToken ct = default);
}

public interface IUserService
{
    Task<UserDto> GetMeAsync(CancellationToken ct = default);
    Task<PreferencesDto> GetPreferencesAsync(CancellationToken ct = default);
    Task<PreferencesDto> UpdatePreferencesAsync(PreferencesDto request, CancellationToken ct = default);
}

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ActivityDto>> GetActivityAsync(int limit, CancellationToken ct = default);
}

public interface IClassroomService
{
    Task<PagedResult<ClassroomDto>> ListAsync(ClassroomQuery query, CancellationToken ct = default);
    Task<ClassroomDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<ClassroomDto> CreateAsync(ClassroomRequest request, CancellationToken ct = default);
    Task<ClassroomDto> UpdateAsync(Guid id, ClassroomRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<ClassroomHistoryDto>> GetHistoryAsync(Guid id, int page, int pageSize, CancellationToken ct = default);
    Task<PagedResult<AlertDto>> GetAlertsAsync(Guid id, int page, int pageSize, CancellationToken ct = default);
}

public interface IAlertService
{
    Task<PagedResult<AlertDto>> ListAsync(AlertFilter filter, CancellationToken ct = default);
    Task<AlertDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<AlertDto> CreateAsync(CreateAlertRequest request, string source = EventSources.User, CancellationToken ct = default);
    Task<DeviceAlertResult> CreateFromDeviceAsync(Guid deviceId, DeviceAlertRequest request, CancellationToken ct = default);
    Task<AlertDto> AcknowledgeAsync(Guid id, CancellationToken ct = default);
    Task<AlertDto> ResolveAsync(Guid id, ResolveAlertRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AlertEventDto>> GetHistoryAsync(Guid id, CancellationToken ct = default);
}

public interface IDeviceService
{
    Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct = default);
    Task<DeviceDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<DeviceCredentialResponse> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default);
    Task<DeviceCredentialResponse> RotateKeyAsync(Guid id, CancellationToken ct = default);
    Task<DeviceDto> RevokeCredentialsAsync(Guid id, CancellationToken ct = default);
    Task<DeviceHistoryDto> GetHistoryAsync(Guid id, DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Records a heartbeat. The caller must already have authenticated the device.</summary>
    Task<DeviceDto> RecordHeartbeatAsync(Guid deviceId, HeartbeatRequest request, CancellationToken ct = default);

    /// <summary>Marks silent devices offline (once per outage). Called by the background monitor.</summary>
    Task<int> DetectOfflineDevicesAsync(CancellationToken ct = default);

    /// <summary>Deletes heartbeat observations older than the configured retention window.</summary>
    Task<int> PurgeOldHeartbeatsAsync(CancellationToken ct = default);
}

public interface IReportService
{
    Task<AlertReportDto> GetAlertsAsync(AlertFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<CountByClassroomDto>> GetByClassroomAsync(AlertFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<CountByTypeDto>> GetByTypeAsync(AlertFilter filter, CancellationToken ct = default);
    Task<ResponseTimeReportDto> GetResponseTimesAsync(AlertFilter filter, CancellationToken ct = default);
    Task<PagedResult<ClassroomHistoryReportRowDto>> GetClassroomHistoryAsync(AlertFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceUptimeDto>> GetDeviceUptimeAsync(AlertFilter filter, CancellationToken ct = default);
    Task<ReportDashboardDto> GetDashboardAsync(AlertFilter filter, CancellationToken ct = default);
    Task ExportAlertsAsync(AlertFilter filter, bool csv, Stream output, CancellationToken ct = default);
}

public interface IDemoService
{
    Task<AlertDto> SimulateAlertAsync(CancellationToken ct = default);
    Task<int> ResetAsync(CancellationToken ct = default);
}
