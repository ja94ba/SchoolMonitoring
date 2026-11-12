using Microsoft.EntityFrameworkCore;
using SafeClass.Application.DTOs;
using SafeClass.Application.Exceptions;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;
using Xunit;

namespace SafeClass.UnitTests;

public class AlertLifecycleTests
{
    private static CreateAlertRequest Req(Guid room, string type = AlertTypes.SilentPanicButton) => new() { ClassroomId = room, AlertType = type };

    [Fact]
    public async Task Create_sets_active_alert_and_emergency_status_and_writes_history()
    {
        using var h = new Harness();
        using var db = h.NewDb();

        var alert = await h.Alerts(db).CreateAsync(Req(h.Room1.Id));

        Assert.Equal(AlertStatus.Active, alert.Status);
        using var check = h.NewDb();
        Assert.Equal(ClassroomStatus.Emergency, (await check.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus);
        Assert.Single(await check.ClassroomStatusHistory.Where(x => x.ClassroomId == h.Room1.Id).ToListAsync());
        Assert.Single(await check.AlertEventHistory.Where(x => x.AlertId == alert.Id).ToListAsync());
        Assert.Contains("created:A-101", h.Notifier.Events);
    }

    [Fact]
    public async Task Acknowledge_keeps_room_in_emergency_and_records_who_and_when()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var svc = h.Alerts(db);
        var alert = await svc.CreateAsync(Req(h.Room1.Id));
        h.Time.Advance(TimeSpan.FromSeconds(90));

        var acked = await svc.AcknowledgeAsync(alert.Id);

        Assert.Equal(AlertStatus.Acknowledged, acked.Status);
        Assert.Equal("Rita Responder", acked.AcknowledgedBy);
        Assert.Equal(90_000, acked.AckAt!.Value - acked.ReceivedAt);
        using var check = h.NewDb();
        Assert.Equal(ClassroomStatus.Emergency, (await check.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus);
    }

    [Fact]
    public async Task Resolve_returns_room_to_normal_and_is_recorded()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var svc = h.Alerts(db);
        var alert = await svc.CreateAsync(Req(h.Room1.Id));
        await svc.AcknowledgeAsync(alert.Id);
        h.Time.Advance(TimeSpan.FromMinutes(5));

        var resolved = await svc.ResolveAsync(alert.Id, new ResolveAlertRequest { Notes = "False alarm" });

        Assert.Equal(AlertStatus.Resolved, resolved.Status);
        Assert.Equal("False alarm", resolved.ResolutionNotes);
        using var check = h.NewDb();
        Assert.Equal(ClassroomStatus.Normal, (await check.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus);
        Assert.Contains("resolved:A-101", h.Notifier.Events);
    }

    [Fact]
    public async Task Invalid_transitions_are_rejected_with_conflict()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var svc = h.Alerts(db);
        var alert = await svc.CreateAsync(Req(h.Room1.Id));
        await svc.AcknowledgeAsync(alert.Id);

        await Assert.ThrowsAsync<ConflictException>(() => svc.AcknowledgeAsync(alert.Id));          // ack twice
        await svc.ResolveAsync(alert.Id, new ResolveAlertRequest());
        await Assert.ThrowsAsync<ConflictException>(() => svc.ResolveAsync(alert.Id, new ResolveAlertRequest())); // resolve twice
        await Assert.ThrowsAsync<ConflictException>(() => svc.AcknowledgeAsync(alert.Id));          // ack after resolve
    }

    [Fact]
    public async Task Resolving_an_active_alert_records_an_implicit_acknowledgement()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var svc = h.Alerts(db);
        var alert = await svc.CreateAsync(Req(h.Room1.Id));

        var resolved = await svc.ResolveAsync(alert.Id, new ResolveAlertRequest());

        Assert.NotNull(resolved.AckAt);
        var events = await svc.GetHistoryAsync(alert.Id);
        Assert.Contains(events, e => e.Notes != null && e.Notes.Contains("Implicitly acknowledged"));
    }

    [Fact]
    public async Task Multiple_alerts_in_one_room_keep_it_in_emergency_until_the_last_is_resolved()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var svc = h.Alerts(db);
        var first = await svc.CreateAsync(Req(h.Room1.Id));
        var second = await svc.CreateAsync(Req(h.Room1.Id, AlertTypes.MedicalAssistance));

        await svc.ResolveAsync(first.Id, new ResolveAlertRequest());
        using (var mid = h.NewDb())
            Assert.Equal(ClassroomStatus.Emergency, (await mid.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus);

        await svc.ResolveAsync(second.Id, new ResolveAlertRequest());
        using var end = h.NewDb();
        Assert.Equal(ClassroomStatus.Normal, (await end.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus);
    }

    [Fact]
    public async Task Status_history_is_written_only_on_real_changes()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var svc = h.Alerts(db);
        var first = await svc.CreateAsync(Req(h.Room1.Id));
        var second = await svc.CreateAsync(Req(h.Room1.Id));   // already Emergency: no new row
        await svc.AcknowledgeAsync(first.Id);                   // ack never changes status

        using var check = h.NewDb();
        Assert.Single(await check.ClassroomStatusHistory.Where(x => x.ClassroomId == h.Room1.Id).ToListAsync());
    }

    [Fact]
    public async Task Offline_device_with_active_emergency_stays_emergency_and_recovers_to_offline_not_normal()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var alerts = h.Alerts(db);
        var alert = await alerts.CreateAsync(Req(h.Room1.Id));

        h.Time.Advance(TimeSpan.FromMinutes(5));                 // device goes silent
        await h.Devices(db).DetectOfflineDevicesAsync();
        using (var c1 = h.NewDb())
        {
            Assert.False((await c1.Devices.FindAsync(h.Device1.Id))!.IsOnline);
            Assert.Equal(ClassroomStatus.Emergency, (await c1.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus); // emergency wins over offline
        }

        await alerts.ResolveAsync(alert.Id, new ResolveAlertRequest());   // nothing left, but the device is still offline
        using var c2 = h.NewDb();
        Assert.Equal(ClassroomStatus.Offline, (await c2.Classrooms.FindAsync(h.Room1.Id))!.CurrentStatus);
    }

    [Fact]
    public async Task Alert_creation_survives_a_failing_notifier()
    {
        using var h = new Harness();
        h.Notifier.Throw = true;
        using var db = h.NewDb();

        var alert = await h.Alerts(db).CreateAsync(Req(h.Room1.Id));   // must not throw: commit already happened

        using var check = h.NewDb();
        Assert.NotNull(await check.EmergencyAlerts.FindAsync(alert.Id));
    }

    [Fact]
    public async Task Unknown_alert_type_is_a_validation_error()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        await Assert.ThrowsAsync<AppValidationException>(() => h.Alerts(db).CreateAsync(Req(h.Room1.Id, "Made Up Type")));
    }
}
