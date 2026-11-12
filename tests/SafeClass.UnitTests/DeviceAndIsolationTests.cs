using Microsoft.EntityFrameworkCore;
using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;
using SafeClass.Application.Security;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;
using Xunit;

namespace SafeClass.UnitTests;

public class DeviceAndIsolationTests
{
    [Fact]
    public async Task Duplicate_esp32_submission_returns_the_same_alert_and_creates_one_row()
    {
        using var h = new Harness();
        h.ActAsDevice(h.Device1);
        using var db = h.NewDb();
        var req = new DeviceAlertRequest { AlertType = AlertTypes.SilentPanicButton, EventId = "evt-0001" };

        var first = await h.Alerts(db).CreateFromDeviceAsync(h.Device1.Id, req);
        var retry = await h.Alerts(db).CreateFromDeviceAsync(h.Device1.Id, req);

        Assert.False(first.Duplicate);
        Assert.True(retry.Duplicate);
        Assert.Equal(first.Alert.Id, retry.Alert.Id);
        using var check = h.NewDb();
        Assert.Equal(1, await check.EmergencyAlerts.CountAsync(a => a.DeviceId == h.Device1.Id));
        Assert.Equal(1, h.Notifier.Events.Count(e => e == "created:A-101"));
    }

    [Fact]
    public async Task Different_event_ids_create_separate_alerts()
    {
        using var h = new Harness();
        h.ActAsDevice(h.Device1);
        using var db = h.NewDb();
        var svc = h.Alerts(db);
        await svc.CreateFromDeviceAsync(h.Device1.Id, new DeviceAlertRequest { AlertType = AlertTypes.SilentPanicButton, EventId = "evt-1" });
        await svc.CreateFromDeviceAsync(h.Device1.Id, new DeviceAlertRequest { AlertType = AlertTypes.SilentPanicButton, EventId = "evt-2" });
        using var check = h.NewDb();
        Assert.Equal(2, await check.EmergencyAlerts.CountAsync(a => a.DeviceId == h.Device1.Id));
    }

    [Fact]
    public async Task A_device_cannot_submit_alerts_for_another_device()
    {
        using var h = new Harness();
        h.ActAsDevice(h.Device1);
        using var db = h.NewDb();
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            h.Alerts(db).CreateFromDeviceAsync(h.Device2.Id, new DeviceAlertRequest { AlertType = AlertTypes.SilentPanicButton, EventId = "x" }));
    }

    [Fact]
    public async Task Revoked_device_cannot_submit_alerts_or_heartbeats()
    {
        using var h = new Harness();
        // revoke through the service as an administrator
        var adminUser = new User { Id = Guid.NewGuid(), SchoolId = h.SchoolA.Id, FullName = "Admin", Email = "admin@a.test", Role = UserRole.Administrator, PasswordHash = "x" };
        using (var seed = h.NewDb()) { seed.Users.Add(adminUser); await seed.SaveChangesAsync(); }
        h.ActAs(adminUser);
        using var db2 = h.NewDb();
        await h.Devices(db2).RevokeCredentialsAsync(h.Device1.Id);

        h.ActAsDevice(h.Device1);
        using var db3 = h.NewDb();
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Devices(db3).RecordHeartbeatAsync(h.Device1.Id, new HeartbeatRequest { SignalStrength = 3 }));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            h.Alerts(db3).CreateFromDeviceAsync(h.Device1.Id, new DeviceAlertRequest { AlertType = AlertTypes.SilentPanicButton, EventId = "y" }));
    }

    [Fact]
    public async Task Offline_detection_runs_once_per_outage_and_heartbeat_restores_online()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var devices = h.Devices(db);

        h.Time.Advance(TimeSpan.FromMinutes(3));
        var first = await devices.DetectOfflineDevicesAsync();
        var second = await devices.DetectOfflineDevicesAsync();
        Assert.True(first >= 2);
        Assert.Equal(0, second);

        using (var c = h.NewDb())
            Assert.Equal(1, await c.DeviceConnectionHistory.CountAsync(x => x.DeviceId == h.Device1.Id && !x.IsOnline));

        h.ActAsDevice(h.Device1);
        await devices.RecordHeartbeatAsync(h.Device1.Id, new HeartbeatRequest { SignalStrength = 3 });
        using var check = h.NewDb();
        Assert.True((await check.Devices.FindAsync(h.Device1.Id))!.IsOnline);
        Assert.Equal(ClassroomStatus.Normal, (await check.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus);
    }

    [Fact]
    public async Task Heartbeat_history_is_throttled()
    {
        using var h = new Harness();
        h.ActAsDevice(h.Device1);
        using var db = h.NewDb();
        var devices = h.Devices(db);
        for (var i = 0; i < 5; i++) { await devices.RecordHeartbeatAsync(h.Device1.Id, new HeartbeatRequest { SignalStrength = 3 }); h.Time.Advance(TimeSpan.FromSeconds(10)); }
        using var check = h.NewDb();
        Assert.True(await check.DeviceHeartbeatHistory.CountAsync(x => x.DeviceId == h.Device1.Id) <= 2);
    }

    [Fact]
    public async Task Users_cannot_read_or_change_another_schools_data()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        // Responder belongs to school A
        await Assert.ThrowsAsync<NotFoundException>(() => h.Classrooms(db).GetAsync(h.OtherRoom.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Alerts(db).CreateAsync(new CreateAlertRequest { ClassroomId = h.OtherRoom.Id, AlertType = AlertTypes.SilentPanicButton }));
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Classrooms(db).ListAsync(new ClassroomQuery { SchoolId = h.SchoolB.Id }));

        var list = await h.Classrooms(db).ListAsync(new ClassroomQuery());
        Assert.DoesNotContain(list.Items, c => c.Id == h.OtherRoom.Id);
        Assert.Equal(2, list.TotalCount);
    }

    [Fact]
    public async Task Alerts_of_another_school_are_invisible()
    {
        using var h = new Harness();
        h.ActAs(h.OtherSchoolAdmin);
        using var db = h.NewDb();
        var theirs = await h.Alerts(db).CreateAsync(new CreateAlertRequest { ClassroomId = h.OtherRoom.Id, AlertType = AlertTypes.SilentPanicButton });

        h.ActAs(h.Responder);
        await Assert.ThrowsAsync<NotFoundException>(() => h.Alerts(db).GetAsync(theirs.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Alerts(db).AcknowledgeAsync(theirs.Id));
        var mine = await h.Alerts(db).ListAsync(new AlertFilter());
        Assert.Empty(mine.Items);
    }

    [Fact]
    public async Task Device_key_hash_verification_works_and_plain_key_is_not_stored()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var device = await db.Devices.FirstAsync(d => d.Id == h.Device1.Id);
        Assert.True(DeviceCredentials.Verify(Harness.DeviceKey, device.ApiKeyHash));
        Assert.False(DeviceCredentials.Verify("wrong", device.ApiKeyHash));
        Assert.DoesNotContain(Harness.DeviceKey, device.ApiKeyHash);
    }

    [Fact]
    public void Password_hash_round_trips_and_rejects_wrong_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery");
        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.NotEqual(PasswordHasher.Hash("correct horse battery"), hash);   // salted
    }
}
