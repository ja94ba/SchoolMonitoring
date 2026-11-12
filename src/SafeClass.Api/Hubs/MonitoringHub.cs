using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SafeClass.Application.DTOs;
using SafeClass.Application.Interfaces;

namespace SafeClass.Api.Hubs;

/// <summary>
/// Change notifications only. Clients join the group of their own school (taken from the validated token, never
/// from client input), so they only receive events for schools they may access. State changes always go through REST.
/// </summary>
[Authorize]
public sealed class MonitoringHub : Hub
{
    public static string GroupName(Guid schoolId) => $"school:{schoolId}";

    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.User?.FindFirstValue("school_id"), out var schoolId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(schoolId));
            await base.OnConnectedAsync();
        }
        else
        {
            Context.Abort();
        }
    }
}

public sealed class SignalRMonitoringNotifier(IHubContext<MonitoringHub> hub) : IMonitoringNotifier
{
    private IClientProxy Group(Guid schoolId) => hub.Clients.Group(MonitoringHub.GroupName(schoolId));

    public Task ClassroomStatusChangedAsync(Guid schoolId, ClassroomDto classroom, CancellationToken ct = default) =>
        Group(schoolId).SendAsync("ClassroomStatusChanged", classroom, ct);

    public Task AlertCreatedAsync(Guid schoolId, AlertDto alert, CancellationToken ct = default) =>
        Group(schoolId).SendAsync("EmergencyAlertCreated", alert, ct);

    public Task AlertAcknowledgedAsync(Guid schoolId, AlertDto alert, CancellationToken ct = default) =>
        Group(schoolId).SendAsync("EmergencyAlertAcknowledged", alert, ct);

    public Task AlertResolvedAsync(Guid schoolId, AlertDto alert, CancellationToken ct = default) =>
        Group(schoolId).SendAsync("EmergencyAlertResolved", alert, ct);

    public Task DeviceConnectionChangedAsync(Guid schoolId, DeviceDto device, CancellationToken ct = default) =>
        Group(schoolId).SendAsync("DeviceConnectionChanged", device, ct);
}
