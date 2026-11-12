using SafeClass.Application.DTOs;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;
using Xunit;

namespace SafeClass.UnitTests;

public class DashboardAndReportTests
{
    [Fact]
    public async Task Dashboard_summary_counts_rooms_and_open_alerts()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var alerts = h.Alerts(db);
        var a = await alerts.CreateAsync(new CreateAlertRequest { ClassroomId = h.Room1.Id, AlertType = AlertTypes.SilentPanicButton });

        var s1 = await h.Dashboard(db).GetSummaryAsync();
        Assert.Equal(2, s1.TotalClassrooms);
        Assert.Equal(1, s1.EmergencyClassrooms);
        Assert.Equal(1, s1.NormalClassrooms);
        Assert.Equal(1, s1.ActiveAlerts);
        Assert.Equal(1, s1.OpenAlerts);

        await alerts.AcknowledgeAsync(a.Id);
        var s2 = await h.Dashboard(db).GetSummaryAsync();
        Assert.Equal(0, s2.ActiveAlerts);
        Assert.Equal(1, s2.AcknowledgedAlerts);
        Assert.Equal(1, s2.OpenAlerts);
    }

    [Fact]
    public async Task Response_time_report_averages_complete_incidents_and_counts_incomplete_ones_separately()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var alerts = h.Alerts(db);

        var a1 = await alerts.CreateAsync(new CreateAlertRequest { ClassroomId = h.Room1.Id, AlertType = AlertTypes.SilentPanicButton });
        h.Time.Advance(TimeSpan.FromSeconds(60)); await alerts.AcknowledgeAsync(a1.Id);
        h.Time.Advance(TimeSpan.FromSeconds(240)); await alerts.ResolveAsync(a1.Id, new ResolveAlertRequest());   // ack 60 s, resolve 300 s

        var a2 = await alerts.CreateAsync(new CreateAlertRequest { ClassroomId = h.Room2.Id, AlertType = AlertTypes.MedicalAssistance });
        h.Time.Advance(TimeSpan.FromSeconds(120)); await alerts.AcknowledgeAsync(a2.Id);                          // ack 120 s, never resolved

        var report = await h.Reports(db).GetResponseTimesAsync(new AlertFilter());

        Assert.Equal(2, report.TotalIncidents);
        Assert.Equal(2, report.Acknowledgement.Count);
        Assert.Equal(90, report.Acknowledgement.AverageSeconds!.Value, 1);
        Assert.Equal(60, report.Acknowledgement.FastestSeconds!.Value, 1);
        Assert.Equal(120, report.Acknowledgement.SlowestSeconds!.Value, 1);
        Assert.Equal(1, report.Resolution.Count);
        Assert.Equal(300, report.Resolution.AverageSeconds!.Value, 1);
        Assert.Equal(1, report.IncompleteResolution);   // the unresolved incident is reported, not averaged in as zero
        Assert.Equal(0, report.IncompleteAcknowledgement);
    }

    [Fact]
    public async Task Response_time_report_has_null_averages_when_there_is_no_data()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var report = await h.Reports(db).GetResponseTimesAsync(new AlertFilter());
        Assert.Equal(0, report.TotalIncidents);
        Assert.Null(report.Acknowledgement.AverageSeconds);
        Assert.Null(report.Resolution.AverageSeconds);
    }

    [Fact]
    public async Task Date_range_filter_limits_alert_reports()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var alerts = h.Alerts(db);
        var march2 = h.Time.GetUtcNow().UtcDateTime.Date;

        await alerts.CreateAsync(new CreateAlertRequest { ClassroomId = h.Room1.Id, AlertType = AlertTypes.SilentPanicButton });
        h.Time.Advance(TimeSpan.FromDays(10));
        await alerts.CreateAsync(new CreateAlertRequest { ClassroomId = h.Room2.Id, AlertType = AlertTypes.MedicalAssistance });

        var early = await h.Reports(db).GetAlertsAsync(new AlertFilter { StartDate = march2, EndDate = march2 });   // end date covers the whole day
        var late = await h.Reports(db).GetAlertsAsync(new AlertFilter { StartDate = march2.AddDays(5) });
        var all = await h.Reports(db).GetAlertsAsync(new AlertFilter());

        Assert.Equal(1, early.TotalEmergencies);
        Assert.Equal("A-101", early.Alerts.Items.Single().ClassroomCode);
        Assert.Equal(1, late.TotalEmergencies);
        Assert.Equal(2, all.TotalEmergencies);

        var byType = await h.Reports(db).GetByTypeAsync(new AlertFilter());
        Assert.Equal(2, byType.Count);
        var byRoom = await h.Reports(db).GetByClassroomAsync(new AlertFilter());
        Assert.Equal(2, byRoom.Count);
    }

    [Fact]
    public async Task Csv_export_quotes_fields_and_neutralises_spreadsheet_formulas()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var alerts = h.Alerts(db);
        var a = await alerts.CreateAsync(new CreateAlertRequest { ClassroomId = h.Room1.Id, AlertType = AlertTypes.SilentPanicButton });
        await alerts.ResolveAsync(a.Id, new ResolveAlertRequest { Notes = "=HYPERLINK(\"http://evil\"),\nline two" });

        using var ms = new MemoryStream();
        await h.Reports(db).ExportAlertsAsync(new AlertFilter(), csv: true, ms);
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        Assert.Contains("A-101", text);
        Assert.DoesNotContain(",=HYPERLINK", text);          // formula prefix is neutralised
        Assert.Contains("\"\"http://evil\"\"", text);        // quotes doubled inside a quoted field
    }

    [Fact]
    public async Task History_survives_a_new_database_connection_context()
    {
        using var h = new Harness();
        Guid id;
        using (var db = h.NewDb())
        {
            var a = await h.Alerts(db).CreateAsync(new CreateAlertRequest { ClassroomId = h.Room1.Id, AlertType = AlertTypes.SilentPanicButton });
            await h.Alerts(db).AcknowledgeAsync(a.Id);
            id = a.Id;
        }
        using var fresh = h.NewDb();   // a brand-new context sees everything that was committed
        var events = await h.Alerts(fresh).GetHistoryAsync(id);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Classrooms_are_soft_deleted_and_keep_their_incident_history()
    {
        using var h = new Harness();
        using var db = h.NewDb();
        var a = await h.Alerts(db).CreateAsync(new CreateAlertRequest { ClassroomId = h.Room1.Id, AlertType = AlertTypes.SilentPanicButton });
        await h.Alerts(db).ResolveAsync(a.Id, new ResolveAlertRequest());

        await h.Classrooms(db).DeleteAsync(h.Room1.Id);

        var list = await h.Classrooms(db).ListAsync(new ClassroomQuery());
        Assert.DoesNotContain(list.Items, c => c.Id == h.Room1.Id);
        var report = await h.Reports(db).GetAlertsAsync(new AlertFilter());
        Assert.Equal(1, report.TotalEmergencies);   // the incident is still there
    }
}
