using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SafeClass.Api.Services;
using SafeClass.Application.DTOs;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Options;
using SafeClass.Domain.Entities;

namespace SafeClass.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class AuthController(IAuthService auth, IOptions<SafeClassOptions> options) : ControllerBase
{
    /// <summary>Public, non-sensitive settings the frontend needs before sign-in.</summary>
    [HttpGet("config"), AllowAnonymous]
    public ActionResult<AppConfigDto> Config() =>
        new AppConfigDto { DemoMode = options.Value.DemoModeEnabled, AlertTypes = options.Value.AllowedAlertTypes };

    [HttpPost("auth/login"), AllowAnonymous, EnableRateLimiting("login")]
    public Task<LoginResponse> Login(LoginRequest request, CancellationToken ct) => auth.LoginAsync(request, ct);
}

[ApiController, Route("api/v1/dashboard")]
public sealed class DashboardController(IDashboardService dashboard) : ControllerBase
{
    [HttpGet("summary")]
    public Task<DashboardSummaryDto> Summary(CancellationToken ct) => dashboard.GetSummaryAsync(ct);

    [HttpGet("activity")]
    public Task<IReadOnlyList<ActivityDto>> Activity([FromQuery] int limit = 20, CancellationToken ct = default) =>
        dashboard.GetActivityAsync(Math.Clamp(limit, 1, 200), ct);
}

[ApiController, Route("api/v1/schools")]
public sealed class SchoolsController(ISchoolService schools) : ControllerBase
{
    [HttpGet("current")]
    public Task<SchoolDto> Current(CancellationToken ct) => schools.GetCurrentAsync(ct);

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.AdminOnly)]
    public Task<SchoolDto> Update(Guid id, UpdateSchoolRequest request, CancellationToken ct) => schools.UpdateAsync(id, request, ct);
}

[ApiController, Route("api/v1/classrooms")]
public sealed class ClassroomsController(IClassroomService classrooms) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<ClassroomDto>> List([FromQuery] ClassroomQuery query, CancellationToken ct) => classrooms.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<ClassroomDto> Get(Guid id, CancellationToken ct) => classrooms.GetAsync(id, ct);

    [HttpPost, Authorize(Policy = Policies.Manager)]
    public async Task<ActionResult<ClassroomDto>> Create(ClassroomRequest request, CancellationToken ct)
    {
        var created = await classrooms.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.Manager)]
    public Task<ClassroomDto> Update(Guid id, ClassroomRequest request, CancellationToken ct) => classrooms.UpdateAsync(id, request, ct);

    /// <summary>Soft delete: the classroom is deactivated; its incident history is kept.</summary>
    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.Manager)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await classrooms.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/history")]
    public Task<PagedResult<ClassroomHistoryDto>> History(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        classrooms.GetHistoryAsync(id, page, pageSize, ct);

    [HttpGet("{id:guid}/alerts")]
    public Task<PagedResult<AlertDto>> Alerts(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        classrooms.GetAlertsAsync(id, page, pageSize, ct);
}

[ApiController, Route("api/v1/alerts")]
public sealed class AlertsController(IAlertService alerts) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AlertDto>> List([FromQuery] AlertFilter filter, CancellationToken ct) => alerts.ListAsync(filter, ct);

    [HttpGet("{id:guid}")]
    public Task<AlertDto> Get(Guid id, CancellationToken ct) => alerts.GetAsync(id, ct);

    [HttpPost, Authorize(Policy = Policies.AlertCreator)]
    public async Task<ActionResult<AlertDto>> Create(CreateAlertRequest request, CancellationToken ct)
    {
        var created = await alerts.CreateAsync(request, EventSources.User, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPost("{id:guid}/acknowledge"), Authorize(Policy = Policies.Responder)]
    public Task<AlertDto> Acknowledge(Guid id, CancellationToken ct) => alerts.AcknowledgeAsync(id, ct);

    [HttpPost("{id:guid}/resolve"), Authorize(Policy = Policies.Responder)]
    public Task<AlertDto> Resolve(Guid id, [FromBody] ResolveAlertRequest? request, CancellationToken ct) =>
        alerts.ResolveAsync(id, request ?? new ResolveAlertRequest(), ct);

    [HttpGet("{id:guid}/history")]
    public Task<IReadOnlyList<AlertEventDto>> History(Guid id, CancellationToken ct) => alerts.GetHistoryAsync(id, ct);
}

[ApiController, Route("api/v1/devices")]
public sealed class DevicesController(IDeviceService devices, IAlertService alerts, ICurrentUser current) : ControllerBase
{
    // ---- staff endpoints (JWT) ----
    [HttpGet]
    public Task<IReadOnlyList<DeviceDto>> List(CancellationToken ct) => devices.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<DeviceDto> Get(Guid id, CancellationToken ct) => devices.GetAsync(id, ct);

    /// <summary>Registers a device and returns its API key once.</summary>
    [HttpPost, Authorize(Policy = Policies.AdminOnly)]
    public async Task<ActionResult<DeviceCredentialResponse>> Register(RegisterDeviceRequest request, CancellationToken ct)
    {
        var result = await devices.RegisterAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = result.Device.Id }, result);
    }

    [HttpPost("{id:guid}/credentials/rotate"), Authorize(Policy = Policies.AdminOnly)]
    public Task<DeviceCredentialResponse> Rotate(Guid id, CancellationToken ct) => devices.RotateKeyAsync(id, ct);

    [HttpPost("{id:guid}/credentials/revoke"), Authorize(Policy = Policies.AdminOnly)]
    public Task<DeviceDto> Revoke(Guid id, CancellationToken ct) => devices.RevokeCredentialsAsync(id, ct);

    [HttpGet("{id:guid}/alerts")]
    public async Task<PagedResult<AlertDto>> DeviceAlerts(Guid id, [FromQuery] AlertFilter filter, CancellationToken ct)
    {
        // Alerts are tied to a classroom; a device's alerts are those of the room it is installed in.
        var device = await devices.GetAsync(id, ct);
        filter.ClassroomId = device.ClassroomId;
        return await alerts.ListAsync(filter, ct);
    }

    [HttpGet("{id:guid}/history")]
    public Task<DeviceHistoryDto> History(Guid id, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        devices.GetHistoryAsync(id, from, to, page, pageSize, ct);

    // ---- device endpoints (X-Device-Identifier + X-Device-Key) ----
    [HttpPost("heartbeat")]
    [Authorize(AuthenticationSchemes = DeviceKeyAuthenticationHandler.Scheme, Policy = Policies.DeviceOnly)]
    [EnableRateLimiting("device")]
    public Task<DeviceDto> Heartbeat(HeartbeatRequest request, CancellationToken ct) =>
        devices.RecordHeartbeatAsync(current.DeviceId!.Value, request, ct);

    /// <summary>
    /// ESP32 button press. Send a unique eventId per press: retries with the same id return the original alert (200)
    /// instead of creating a second one (201).
    /// </summary>
    [HttpPost("alerts")]
    [Authorize(AuthenticationSchemes = DeviceKeyAuthenticationHandler.Scheme, Policy = Policies.DeviceOnly)]
    [EnableRateLimiting("device")]
    public async Task<ActionResult<DeviceAlertResult>> DeviceAlert(DeviceAlertRequest request, CancellationToken ct)
    {
        var result = await alerts.CreateFromDeviceAsync(current.DeviceId!.Value, request, ct);
        return result.Duplicate ? Ok(result) : StatusCode(StatusCodes.Status201Created, result);
    }
}

[ApiController, Route("api/v1/users")]
public sealed class UsersController(IUserService users) : ControllerBase
{
    [HttpGet("me")]
    public Task<UserDto> Me(CancellationToken ct) => users.GetMeAsync(ct);

    [HttpGet("me/preferences")]
    public Task<PreferencesDto> GetPreferences(CancellationToken ct) => users.GetPreferencesAsync(ct);

    [HttpPut("me/preferences")]
    public Task<PreferencesDto> PutPreferences(PreferencesDto request, CancellationToken ct) => users.UpdatePreferencesAsync(request, ct);
}

[ApiController, Route("api/v1/reports"), Authorize(Policy = Policies.ReportViewer)]
public sealed class ReportsController(IReportService reports) : ControllerBase
{
    [HttpGet("alerts")]
    public Task<AlertReportDto> Alerts([FromQuery] AlertFilter f, CancellationToken ct) => reports.GetAlertsAsync(f, ct);

    [HttpGet("alerts/by-classroom")]
    public Task<IReadOnlyList<CountByClassroomDto>> ByClassroom([FromQuery] AlertFilter f, CancellationToken ct) => reports.GetByClassroomAsync(f, ct);

    [HttpGet("alerts/by-type")]
    public Task<IReadOnlyList<CountByTypeDto>> ByType([FromQuery] AlertFilter f, CancellationToken ct) => reports.GetByTypeAsync(f, ct);

    [HttpGet("response-times")]
    public Task<ResponseTimeReportDto> ResponseTimes([FromQuery] AlertFilter f, CancellationToken ct) => reports.GetResponseTimesAsync(f, ct);

    [HttpGet("classroom-history")]
    public Task<PagedResult<ClassroomHistoryReportRowDto>> ClassroomHistory([FromQuery] AlertFilter f, CancellationToken ct) => reports.GetClassroomHistoryAsync(f, ct);

    [HttpGet("device-uptime")]
    public Task<IReadOnlyList<DeviceUptimeDto>> DeviceUptime([FromQuery] AlertFilter f, CancellationToken ct) => reports.GetDeviceUptimeAsync(f, ct);

    [HttpGet("dashboard")]
    public Task<ReportDashboardDto> Dashboard([FromQuery] AlertFilter f, CancellationToken ct) => reports.GetDashboardAsync(f, ct);

    /// <summary>Streams alerts as JSON (default) or CSV (?format=csv).</summary>
    [HttpGet("export/alerts")]
    public async Task ExportAlerts([FromQuery] AlertFilter f, CancellationToken ct)
    {
        var csv = string.Equals(f.Format, "csv", StringComparison.OrdinalIgnoreCase);
        Response.ContentType = csv ? "text/csv; charset=utf-8" : "application/json";
        Response.Headers.ContentDisposition = $"attachment; filename=\"alerts.{(csv ? "csv" : "json")}\"";
        await reports.ExportAlertsAsync(f, csv, Response.Body, ct);
    }
}

/// <summary>Only reachable when SafeClass:DemoModeEnabled is true AND the school is flagged IsDemo.</summary>
[ApiController, Route("api/v1/demo"), Authorize(Policy = Policies.Manager)]
public sealed class DemoController(IDemoService demo, IOptions<SafeClassOptions> options) : ControllerBase
{
    [HttpPost("simulate-alert")]
    public async Task<ActionResult<AlertDto>> Simulate(CancellationToken ct)
    {
        if (!options.Value.DemoModeEnabled) return NotFound();
        return await demo.SimulateAlertAsync(ct);
    }

    /// <summary>Deletes only demo-generated alerts. Real incident history is never touched.</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        if (!options.Value.DemoModeEnabled) return NotFound();
        var removed = await demo.ResetAsync(ct);
        return Ok(new { removed });
    }
}
