using SafeClass.Domain.Enums;

namespace SafeClass.Application.DTOs;

// Property names follow the frontend's existing data contract (id, code, name, building, floor, adviser,
// device, signal, status, lastUpdate / id, roomId, type, receivedAt, status, ackAt, resolvedAt).
// Timestamps are JavaScript milliseconds since the Unix epoch (UTC).

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
}

// ---------- Auth / users ----------
public sealed class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class LoginResponse
{
    public string Token { get; init; } = "";
    public long ExpiresAt { get; init; }
    public UserDto User { get; init; } = new();
}

public sealed class UserDto
{
    public Guid Id { get; init; }
    public Guid SchoolId { get; init; }
    public string FullName { get; init; } = "";
    public string Email { get; init; } = "";
    public UserRole Role { get; init; }
    public string SchoolName { get; init; } = "";
}

public sealed class PreferencesDto
{
    public bool InAppBannerEnabled { get; set; } = true;
    public bool AlertSoundEnabled { get; set; } = true;
    public bool PushNotificationsEnabled { get; set; } = true;
    public bool SmsEnabled { get; set; }
    public bool DailyEmailSummaryEnabled { get; set; }
}

public sealed class AppConfigDto
{
    public bool DemoMode { get; init; }
    public string[] AlertTypes { get; init; } = [];
}

// ---------- Schools ----------
public sealed class SchoolDto
{
    public Guid Id { get; init; }
    public string SchoolCode { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Address { get; init; }
    public string? City { get; init; }
    public string? Province { get; init; }
    public string? Country { get; init; }
    public bool IsDemo { get; init; }
}

public sealed class UpdateSchoolRequest
{
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? Country { get; set; }
}

// ---------- Classrooms ----------
public sealed class ClassroomDto
{
    public Guid Id { get; init; }
    public Guid SchoolId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Building { get; init; } = "";
    public string Floor { get; init; } = "";
    public string Adviser { get; init; } = "";
    public string? Device { get; init; }
    public Guid? DeviceId { get; init; }
    public string? FirmwareVersion { get; init; }
    public int Signal { get; init; }
    public ClassroomStatus Status { get; init; }
    public long LastUpdate { get; init; }
    public bool IsActive { get; init; }
}

public sealed class ClassroomRequest
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Building { get; set; } = "";
    public string Floor { get; set; } = "";
    public string Adviser { get; set; } = "";
}

public sealed class ClassroomQuery
{
    public Guid? SchoolId { get; set; }
    public ClassroomStatus? Status { get; set; }
    public string? Building { get; set; }
    public string? Floor { get; set; }
    public string? Search { get; set; }
    public bool IncludeInactive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class ClassroomHistoryDto
{
    public long Id { get; init; }
    public ClassroomStatus? PreviousStatus { get; init; }
    public ClassroomStatus NewStatus { get; init; }
    public long ChangedAt { get; init; }
    public string TriggerSource { get; init; } = "";
    public string? TriggeredBy { get; init; }
    public Guid? RelatedAlertId { get; init; }
    public string? Reason { get; init; }
}

// ---------- Alerts ----------
public sealed class AlertDto
{
    public Guid Id { get; init; }
    public Guid RoomId { get; init; }
    public string? RoomCode { get; init; }
    public string? RoomName { get; init; }
    public string? Building { get; init; }
    public string Type { get; init; } = "";
    public AlertStatus Status { get; init; }
    public long ReceivedAt { get; init; }
    public long? AckAt { get; init; }
    public string? AcknowledgedBy { get; init; }
    public long? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
    public string? ResolutionNotes { get; init; }
    public string Source { get; init; } = "";
    public Guid? DeviceId { get; init; }
}

public sealed class CreateAlertRequest
{
    public Guid ClassroomId { get; set; }
    public string AlertType { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class ResolveAlertRequest
{
    public string? Notes { get; set; }
}

public sealed class AlertEventDto
{
    public long Id { get; init; }
    public string EventType { get; init; } = "";
    public AlertStatus? PreviousStatus { get; init; }
    public AlertStatus NewStatus { get; init; }
    public long Timestamp { get; init; }
    public string? PerformedBy { get; init; }
    public string Source { get; init; } = "";
    public string? Notes { get; init; }
}

/// <summary>Shared filter for alert lists and reports. Dates are UTC; an end date without a time covers the whole day.</summary>
public sealed class AlertFilter
{
    public Guid? SchoolId { get; set; }
    public Guid? ClassroomId { get; set; }
    public string? Building { get; set; }
    public string? AlertType { get; set; }
    public AlertStatus? Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Format { get; set; }
}

// ---------- Devices ----------
public sealed class DeviceDto
{
    public Guid Id { get; init; }
    public Guid ClassroomId { get; init; }
    public string? ClassroomCode { get; init; }
    public string DeviceIdentifier { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string DeviceType { get; init; } = "";
    public string? FirmwareVersion { get; init; }
    public int SignalStrength { get; init; }
    public bool IsOnline { get; init; }
    public long? LastHeartbeat { get; init; }
    public long RegisteredAt { get; init; }
    public bool IsActive { get; init; }
    public bool CredentialRevoked { get; init; }
}

public sealed class RegisterDeviceRequest
{
    public Guid ClassroomId { get; set; }
    public string DeviceIdentifier { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? DeviceType { get; set; }
    public string? FirmwareVersion { get; set; }
}

public sealed class DeviceCredentialResponse
{
    public DeviceDto Device { get; init; } = new();

    /// <summary>Shown once. Store it on the device; the server keeps only a hash.</summary>
    public string ApiKey { get; init; } = "";
}

public sealed class HeartbeatRequest
{
    public int SignalStrength { get; set; }
    public string? ReportedStatus { get; set; }
    public string? FirmwareVersion { get; set; }
}

public sealed class DeviceAlertRequest
{
    public string? DeviceIdentifier { get; set; }
    public string AlertType { get; set; } = "";
    public string EventId { get; set; } = "";
}

public sealed class DeviceAlertResult
{
    public AlertDto Alert { get; init; } = new();

    /// <summary>True when this eventId was already processed; the existing alert is returned.</summary>
    public bool Duplicate { get; init; }
}

public sealed class HeartbeatHistoryDto
{
    public long Id { get; init; }
    public long ReceivedAt { get; init; }
    public int SignalStrength { get; init; }
    public string? ReportedStatus { get; init; }
    public string? FirmwareVersion { get; init; }
    public bool IsOnline { get; init; }
}

public sealed class ConnectionEventDto
{
    public long Id { get; init; }
    public bool IsOnline { get; init; }
    public long ChangedAt { get; init; }
    public string? Reason { get; init; }
}

public sealed class DeviceHistoryDto
{
    public PagedResult<HeartbeatHistoryDto> Heartbeats { get; init; } = new();
    public IReadOnlyList<ConnectionEventDto> ConnectionEvents { get; init; } = [];
}

// ---------- Dashboard ----------
public sealed class DashboardSummaryDto
{
    public int TotalClassrooms { get; init; }
    public int NormalClassrooms { get; init; }
    public int EmergencyClassrooms { get; init; }
    public int OfflineClassrooms { get; init; }
    public int ActiveAlerts { get; init; }
    public int AcknowledgedAlerts { get; init; }

    /// <summary>Active + acknowledged (everything not yet resolved).</summary>
    public int OpenAlerts { get; init; }
    public int Buildings { get; init; }
}

public sealed class ActivityDto
{
    public long Id { get; init; }
    public long Ts { get; init; }
    public string Kind { get; init; } = "normal";
    public string Text { get; init; } = "";
    public Guid? ClassroomId { get; init; }
    public Guid? AlertId { get; init; }
}

// ---------- Reports ----------
public sealed class AlertReportRowDto
{
    public Guid Id { get; init; }
    public Guid ClassroomId { get; init; }
    public string ClassroomCode { get; init; } = "";
    public string ClassroomName { get; init; } = "";
    public string Building { get; init; } = "";
    public string AlertType { get; init; } = "";
    public AlertStatus Status { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
    public DateTime? AcknowledgedAtUtc { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }
    public double? AcknowledgementSeconds { get; init; }
    public double? ResolutionSeconds { get; init; }
    public string Source { get; init; } = "";
    public string? ResolutionNotes { get; init; }
}

public sealed class AlertReportDto
{
    public int TotalEmergencies { get; init; }
    public int ActiveCount { get; init; }
    public int AcknowledgedCount { get; init; }
    public int ResolvedCount { get; init; }
    public PagedResult<AlertReportRowDto> Alerts { get; init; } = new();
}

public sealed class CountByClassroomDto
{
    public Guid ClassroomId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Building { get; init; } = "";
    public int Count { get; init; }
}

public sealed class CountByTypeDto
{
    public string AlertType { get; init; } = "";
    public int Count { get; init; }
}

public sealed class DurationStatsDto
{
    /// <summary>Number of incidents that have this milestone. Incomplete incidents are excluded, never counted as zero.</summary>
    public long Count { get; init; }
    public double? AverageSeconds { get; init; }
    public double? FastestSeconds { get; init; }
    public double? SlowestSeconds { get; init; }
}

public sealed class PeriodCountDto
{
    public string Period { get; init; } = "";
    public int Count { get; init; }
}

public sealed class ResponseTimeReportDto
{
    public int TotalIncidents { get; init; }
    public DurationStatsDto Acknowledgement { get; init; } = new();
    public DurationStatsDto Resolution { get; init; } = new();
    public int IncompleteAcknowledgement { get; init; }
    public int IncompleteResolution { get; init; }
    public IReadOnlyList<PeriodCountDto> PerDay { get; init; } = [];
    public IReadOnlyList<PeriodCountDto> PerWeek { get; init; } = [];
    public IReadOnlyList<PeriodCountDto> PerMonth { get; init; } = [];
}

public sealed class ClassroomHistoryReportRowDto
{
    public long Id { get; init; }
    public Guid ClassroomId { get; init; }
    public string ClassroomCode { get; init; } = "";
    public string Building { get; init; } = "";
    public ClassroomStatus? PreviousStatus { get; init; }
    public ClassroomStatus NewStatus { get; init; }
    public DateTime ChangedAtUtc { get; init; }
    public string TriggerSource { get; init; } = "";
    public Guid? RelatedAlertId { get; init; }
    public string? Reason { get; init; }
}

public sealed class DeviceUptimeDto
{
    public Guid DeviceId { get; init; }
    public string DeviceIdentifier { get; init; } = "";
    public string? ClassroomCode { get; init; }
    public string? Building { get; init; }
    public bool CurrentlyOnline { get; init; }
    public int OutageCount { get; init; }
    public double TotalOfflineSeconds { get; init; }
    public double LongestOutageSeconds { get; init; }
    public double UptimePercent { get; init; }
}

public sealed class ReportDashboardDto
{
    public int TotalEmergencies { get; init; }
    public IReadOnlyList<CountByClassroomDto> TopClassrooms { get; init; } = [];
    public IReadOnlyList<CountByTypeDto> ByType { get; init; } = [];
    public ResponseTimeReportDto ResponseTimes { get; init; } = new();
    public IReadOnlyList<DeviceUptimeDto> DeviceUptime { get; init; } = [];
}
