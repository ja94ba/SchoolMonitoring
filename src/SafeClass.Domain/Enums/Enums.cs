namespace SafeClass.Domain.Enums;

public enum UserRole
{
    Administrator,
    SafetyCoordinator,
    SecurityStaff,
    Teacher,
    Viewer
}

/// <summary>
/// Display status of a classroom. Precedence when derived (see StatusEngine):
/// Emergency (any unresolved alert) > Offline (device missing or not reporting) > Normal.
/// </summary>
public enum ClassroomStatus
{
    Normal,
    Emergency,
    Offline
}

public enum AlertStatus
{
    Active,
    Acknowledged,
    Resolved
}
