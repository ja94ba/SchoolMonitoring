using Microsoft.Extensions.Options;
using SafeClass.Application.DTOs;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Options;
using SafeClass.Infrastructure.Data;

namespace SafeClass.Api.Services;

/// <summary>Marks devices offline when their heartbeats stop (once per outage). All rules live in IDeviceService.</summary>
public sealed class OfflineDeviceMonitor(IServiceScopeFactory scopes, IOptions<SafeClassOptions> options, ILogger<OfflineDeviceMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.OfflineCheckIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var changed = await scope.ServiceProvider.GetRequiredService<IDeviceService>().DetectOfflineDevicesAsync(stoppingToken);
                    if (changed > 0) log.LogInformation("Marked {Count} device(s) offline", changed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Offline detection pass failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>Daily purge of old heartbeat observations. Incident history is never deleted.</summary>
public sealed class HeartbeatRetentionService(IServiceScopeFactory scopes, ILogger<HeartbeatRetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var removed = await scope.ServiceProvider.GetRequiredService<IDeviceService>().PurgeOldHeartbeatsAsync(stoppingToken);
                    if (removed > 0) log.LogInformation("Purged {Count} old heartbeat rows", removed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Heartbeat retention pass failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>
/// DEMO ONLY (registered only when SafeClass:DemoModeEnabled is true): sends heartbeats for the seeded demo devices
/// so they stay online, replacing the fake in-browser heartbeats of the prototype. Devices in
/// DemoSeeder.OfflineDeviceIdentifiers stay silent so they remain offline.
/// </summary>
public sealed class DemoHeartbeatSimulator(IServiceScopeFactory scopes, ILogger<DemoHeartbeatSimulator> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rng = new Random();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ISafeClassDbContext>();
                    var devices = scope.ServiceProvider.GetRequiredService<IDeviceService>();
                    var demoSchools = db.Schools.Where(s => s.IsDemo).Select(s => s.Id);
                    var ids = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                        db.Devices.Where(d => d.IsActive && d.CredentialRevokedAtUtc == null && demoSchools.Contains(d.SchoolId))
                            .Select(d => new { d.Id, d.DeviceIdentifier }), stoppingToken);
                    foreach (var d in ids)
                    {
                        if (DemoSeeder.OfflineDeviceIdentifiers.Contains(d.DeviceIdentifier)) continue;
                        await devices.RecordHeartbeatAsync(d.Id, new HeartbeatRequest { SignalStrength = rng.Next(2, 5), ReportedStatus = "ok" }, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Demo heartbeat pass failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
