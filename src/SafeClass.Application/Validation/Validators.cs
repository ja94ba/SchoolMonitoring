using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;

namespace SafeClass.Application.Validation;

/// <summary>Small hand-written validators. Each throws AppValidationException (HTTP 400) with field-level messages.</summary>
public static class Validators
{
    public static string Required(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0) errors[field] = [$"{field} is required."];
        else if (v.Length > maxLength) errors[field] = [$"{field} must be at most {maxLength} characters."];
        return v;
    }

    public static string? Optional(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > maxLength) errors[field] = [$"{field} must be at most {maxLength} characters."];
        return v;
    }

    public static void Throw(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0) throw new AppValidationException(errors);
    }

    public static ClassroomRequest Validate(this ClassroomRequest r)
    {
        var e = new Dictionary<string, string[]>();
        var clean = new ClassroomRequest
        {
            Code = Required(e, "code", r.Code, 20),
            Name = Required(e, "name", r.Name, 100),
            Building = Required(e, "building", r.Building, 60),
            Floor = Required(e, "floor", r.Floor, 40),
            Adviser = Optional(e, "adviser", r.Adviser, 100) ?? ""
        };
        Throw(e);
        return clean;
    }

    public static UpdateSchoolRequest Validate(this UpdateSchoolRequest r)
    {
        var e = new Dictionary<string, string[]>();
        var clean = new UpdateSchoolRequest
        {
            Name = Required(e, "name", r.Name, 120),
            Address = Optional(e, "address", r.Address, 200),
            City = Optional(e, "city", r.City, 80),
            Province = Optional(e, "province", r.Province, 80),
            Country = Optional(e, "country", r.Country, 80)
        };
        Throw(e);
        return clean;
    }

    public static RegisterDeviceRequest Validate(this RegisterDeviceRequest r)
    {
        var e = new Dictionary<string, string[]>();
        var id = Required(e, "deviceIdentifier", r.DeviceIdentifier, 64);
        if (id.Length > 0 && !id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            e["deviceIdentifier"] = ["deviceIdentifier may contain only letters, digits, '-', '_' and '.'."];
        if (r.ClassroomId == Guid.Empty) e["classroomId"] = ["classroomId is required."];
        var clean = new RegisterDeviceRequest
        {
            ClassroomId = r.ClassroomId,
            DeviceIdentifier = id,
            DeviceName = Optional(e, "deviceName", r.DeviceName, 100),
            DeviceType = Optional(e, "deviceType", r.DeviceType, 40),
            FirmwareVersion = Optional(e, "firmwareVersion", r.FirmwareVersion, 40)
        };
        Throw(e);
        return clean;
    }

    public static HeartbeatRequest Validate(this HeartbeatRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (r.SignalStrength is < 0 or > 4) e["signalStrength"] = ["signalStrength must be between 0 and 4."];
        var clean = new HeartbeatRequest
        {
            SignalStrength = r.SignalStrength,
            ReportedStatus = Optional(e, "reportedStatus", r.ReportedStatus, 30),
            FirmwareVersion = Optional(e, "firmwareVersion", r.FirmwareVersion, 40)
        };
        Throw(e);
        return clean;
    }

    public static string ValidateAlertType(string? type, IEnumerable<string> allowed)
    {
        var e = new Dictionary<string, string[]>();
        var t = Required(e, "alertType", type, 64);
        Throw(e);
        var match = allowed.FirstOrDefault(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new AppValidationException("alertType", $"Unknown alertType. Allowed: {string.Join(", ", allowed)}.");
        return match;
    }

    public static string ValidateEventId(string? eventId)
    {
        var e = new Dictionary<string, string[]>();
        var v = Required(e, "eventId", eventId, 100);
        Throw(e);
        return v;
    }
}
