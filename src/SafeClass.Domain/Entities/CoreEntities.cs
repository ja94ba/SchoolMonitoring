using SafeClass.Domain.Enums;

namespace SafeClass.Domain.Entities;

public class School
{
    public Guid Id { get; set; }
    public string SchoolCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? Country { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>True for development/demo fixtures. Demo reset only ever touches demo schools.</summary>
    public bool IsDemo { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class User
{
    public Guid Id { get; set; }
    public Guid SchoolId { get; set; }
    public string FullName { get; set; } = "";

    /// <summary>Stored lower-case. Unique.</summary>
    public string Email { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>PBKDF2 hash. Never returned by the API.</summary>
    public string? PasswordHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class Classroom
{
    public Guid Id { get; set; }
    public Guid SchoolId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Building { get; set; } = "";
    public string Floor { get; set; } = "";
    public string Adviser { get; set; } = "";
    public ClassroomStatus CurrentStatus { get; set; } = ClassroomStatus.Offline;
    public DateTime LastStatusUpdateUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Device> Devices { get; set; } = new List<Device>();
}

public class Device
{
    public Guid Id { get; set; }
    public Guid SchoolId { get; set; }
    public Guid ClassroomId { get; set; }
    public string DeviceIdentifier { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceType { get; set; } = "ESP32";
    public string? FirmwareVersion { get; set; }

    /// <summary>Signal level 0 (none) to 4 (strong). The firmware maps RSSI to this scale.</summary>
    public int SignalStrength { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>SHA-256 (hex) of the device API key. The key itself is never stored.</summary>
    public string ApiKeyHash { get; set; } = "";
    public DateTime? CredentialRevokedAtUtc { get; set; }

    public Classroom? Classroom { get; set; }
}

public class EmergencyAlert
{
    public Guid Id { get; set; }
    public Guid SchoolId { get; set; }
    public Guid ClassroomId { get; set; }
    public Guid? DeviceId { get; set; }

    /// <summary>Free text so new alert types need no schema change.</summary>
    public string AlertType { get; set; } = "";
    public AlertStatus Status { get; set; } = AlertStatus.Active;
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }
    public string Source { get; set; } = EventSources.User;

    /// <summary>Device-supplied event id. (DeviceId, DeviceEventId) is unique, which makes retries idempotent.</summary>
    public string? DeviceEventId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Classroom? Classroom { get; set; }
}

public class NotificationPreference
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public bool InAppBannerEnabled { get; set; } = true;
    public bool AlertSoundEnabled { get; set; } = true;
    public bool PushNotificationsEnabled { get; set; } = true;
    public bool SmsEnabled { get; set; }
    public bool DailyEmailSummaryEnabled { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
