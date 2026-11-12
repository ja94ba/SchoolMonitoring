namespace SafeClass.Domain.Entities;

/// <summary>Where an alert or status change came from.</summary>
public static class EventSources
{
    public const string Device = "device";
    public const string User = "user";
    public const string System = "system";
    public const string Demo = "demo";
    public const string Seed = "seed";
}

public static class AlertEventTypes
{
    public const string Created = "alert.created";
    public const string Acknowledged = "alert.acknowledged";
    public const string Resolved = "alert.resolved";
}

public static class ActivityEventTypes
{
    public const string AlertCreated = "alert.created";
    public const string AlertAcknowledged = "alert.acknowledged";
    public const string AlertResolved = "alert.resolved";
    public const string DeviceOnline = "device.online";
    public const string DeviceOffline = "device.offline";
    public const string DeviceRegistered = "device.registered";
    public const string ClassroomCreated = "classroom.created";
}

public static class AlertTypes
{
    public const string SilentPanicButton = "Silent Panic Button";
    public const string MedicalAssistance = "Medical Assistance";
    public const string SuspiciousPerson = "Suspicious Person";
    public const string FireSmoke = "Fire / Smoke Suspected";

    public static readonly string[] Defaults = [SilentPanicButton, MedicalAssistance, SuspiciousPerson, FireSmoke];
}
