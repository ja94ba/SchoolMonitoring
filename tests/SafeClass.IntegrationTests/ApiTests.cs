using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SafeClass.Infrastructure.Data;
using Xunit;

namespace SafeClass.IntegrationTests;

[Collection("api")]
public class ApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Anonymous_requests_are_rejected_but_config_and_health_are_public()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/classrooms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/dashboard/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/v1/config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Login_rejects_bad_credentials_with_a_generic_message()
    {
        var client = _factory.CreateClient();
        var bad = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = DemoSeeder.AdminEmail, password = "wrong" });
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@x.test", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(Strip(await bad.Content.ReadAsStringAsync()), Strip(await unknown.Content.ReadAsStringAsync()));
    }

    private static string Strip(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\"traceId\":\"[^\"]*\"", "");

    [Fact]
    public async Task Classroom_list_uses_the_frontend_contract_and_server_pagination()
    {
        var client = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/classrooms?pageSize=5&page=1");
        Assert.Equal(12, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, page.GetProperty("items").GetArrayLength());
        var first = page.GetProperty("items")[0];
        foreach (var p in new[] { "id", "code", "name", "building", "floor", "adviser", "device", "signal", "status", "lastUpdate" })
            Assert.True(first.TryGetProperty(p, out _), "missing property " + p);
        Assert.Equal(JsonValueKind.Number, first.GetProperty("lastUpdate").ValueKind);   // JS milliseconds
        Assert.Contains(first.GetProperty("status").GetString(), new[] { "normal", "emergency", "offline" });

        var filtered = await client.GetFromJsonAsync<JsonElement>("/api/v1/classrooms?status=offline");
        Assert.Equal(2, filtered.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Alert_lifecycle_over_http_including_invalid_transitions()
    {
        var client = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var room = await ApiFactory.RoomIdAsync(client, "A-101");

        var created = await client.PostAsJsonAsync("/api/v1/alerts", new { classroomId = room, alertType = "Silent Panic Button" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var alert = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = alert.GetProperty("id").GetGuid();
        Assert.Equal("active", alert.GetProperty("status").GetString());

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard/summary");
        Assert.True(summary.GetProperty("activeAlerts").GetInt32() >= 2);   // seeded A-201 + this one

        var ack = await client.PostAsync($"/api/v1/alerts/{id}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/alerts/{id}/acknowledge", null)).StatusCode);

        var resolve = await client.PostAsJsonAsync($"/api/v1/alerts/{id}/resolve", new { notes = "ok" });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/alerts/{id}/resolve", new { })).StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/classrooms/{room}");
        Assert.Equal("normal", detail.GetProperty("status").GetString());

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/alerts/{id}/history");
        Assert.True(history.GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Role_boundaries_are_enforced()
    {
        var viewer = await _factory.LoginAsync(DemoSeeder.ViewerEmail);
        var teacher = await _factory.LoginAsync(DemoSeeder.TeacherEmail);
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var room = await ApiFactory.RoomIdAsync(admin, "A-102");
        var existing = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/alerts?status=active")).GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/alerts", new { classroomId = room, alertType = "Silent Panic Button" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/alerts/{existing}/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.PostAsync($"/api/v1/alerts/{existing}/resolve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/classrooms", new { code = "X-1", name = "x", building = "b", floor = "f", adviser = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/devices", new { classroomId = room, deviceIdentifier = "ESP32-NEW" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.GetAsync("/api/v1/reports/alerts")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/reports/alerts")).StatusCode);   // read-only role may view reports
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/classrooms")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await teacher.PostAsJsonAsync("/api/v1/alerts", new { classroomId = room, alertType = "Medical Assistance" })).StatusCode);
    }

    [Fact]
    public async Task Staff_token_cannot_use_device_endpoints_and_device_key_cannot_use_staff_endpoints()
    {
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        Assert.True((await admin.PostAsJsonAsync("/api/v1/devices/heartbeat", new { signalStrength = 3 })).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        var device = _factory.DeviceClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/v1/classrooms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsync("/api/v1/alerts/" + Guid.NewGuid() + "/acknowledge", null)).StatusCode);
    }

    [Fact]
    public async Task Device_heartbeat_and_alert_with_idempotent_retry()
    {
        var device = _factory.DeviceClient("ESP32-A101");
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/devices/heartbeat", new { signalStrength = 3 })).StatusCode);

        var body = new { alertType = "Silent Panic Button", eventId = "evt-" + Guid.NewGuid().ToString("N") };
        var first = await device.PostAsJsonAsync("/api/v1/devices/alerts", body);
        var retry = await device.PostAsJsonAsync("/api/v1/devices/alerts", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var a = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alert").GetProperty("id").GetGuid();
        var b = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alert").GetProperty("id").GetGuid();
        Assert.Equal(a, b);

        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var room = await ApiFactory.RoomIdAsync(admin, "A-101");
        var alerts = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/classrooms/{room}/alerts");
        Assert.Equal(1, alerts.GetProperty("items").EnumerateArray().Count(x => x.GetProperty("id").GetGuid() == a));
    }

    [Fact]
    public async Task Wrong_unknown_or_revoked_device_keys_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.DeviceClient("ESP32-A101", "wrong").PostAsJsonAsync("/api/v1/devices/heartbeat", new { signalStrength = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.DeviceClient("ESP32-NOPE").PostAsJsonAsync("/api/v1/devices/heartbeat", new { signalStrength = 3 })).StatusCode);

        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var devices = await admin.GetFromJsonAsync<JsonElement>("/api/v1/devices");
        var id = devices.EnumerateArray().First(d => d.GetProperty("deviceIdentifier").GetString() == "ESP32-A102").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/devices/{id}/credentials/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.DeviceClient("ESP32-A102").PostAsJsonAsync("/api/v1/devices/heartbeat", new { signalStrength = 3 })).StatusCode);
    }

    [Fact]
    public async Task Registering_a_device_returns_the_key_once_and_it_works()
    {
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        // Demo classrooms already have devices, so use a fresh classroom.
        var created = await admin.PostAsJsonAsync("/api/v1/classrooms", new { code = "N-100", name = "New room", building = "Building N", floor = "1st floor", adviser = "Ms. New" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var newRoom = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var reg = await admin.PostAsJsonAsync("/api/v1/devices", new { classroomId = newRoom, deviceIdentifier = "ESP32-N100" });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var key = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        Assert.StartsWith("scd_", key);

        Assert.Equal(HttpStatusCode.OK, (await _factory.DeviceClient("ESP32-N100", key).PostAsJsonAsync("/api/v1/devices/heartbeat", new { signalStrength = 4 })).StatusCode);

        var list = await admin.GetStringAsync("/api/v1/devices");
        Assert.DoesNotContain(key, list);   // the key is never returned again
    }

    [Fact]
    public async Task School_isolation_over_http()
    {
        var (otherEmail, otherRoom) = await _factory.AddOtherSchoolAsync();
        var other = await _factory.LoginAsync(otherEmail);
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var ourRoom = await ApiFactory.RoomIdAsync(admin, "A-101");

        var theirs = await other.GetFromJsonAsync<JsonElement>("/api/v1/classrooms");
        Assert.Equal(1, theirs.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/classrooms/{ourRoom}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/v1/alerts", new { classroomId = ourRoom, alertType = "Silent Panic Button" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/v1/classrooms/{ourRoom}", new { code = "A-101", name = "hack", building = "b", floor = "f", adviser = "a" })).StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/v1/alerts")).GetProperty("totalCount").GetInt32());

        var ours = await admin.GetFromJsonAsync<JsonElement>("/api/v1/classrooms");
        Assert.DoesNotContain(ours.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetGuid() == otherRoom);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/classrooms?schoolId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Validation_errors_are_problem_json_without_internals()
    {
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var res = await admin.PostAsJsonAsync("/api/v1/classrooms", new { code = "", name = "", building = "", floor = "", adviser = "" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("StackTrace", text);
        Assert.DoesNotContain("   at ", text);

        var badAlert = await admin.PostAsJsonAsync("/api/v1/alerts", new { classroomId = Guid.NewGuid(), alertType = "Nope" });
        Assert.Equal(HttpStatusCode.BadRequest, badAlert.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/alerts/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Reports_and_exports_work()
    {
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var rt = await admin.GetFromJsonAsync<JsonElement>("/api/v1/reports/response-times");
        Assert.True(rt.GetProperty("totalIncidents").GetInt32() >= 5);
        Assert.True(rt.GetProperty("acknowledgement").GetProperty("averageSeconds").GetDouble() > 0);

        foreach (var path in new[] { "alerts", "alerts/by-classroom", "alerts/by-type", "classroom-history", "device-uptime", "dashboard" })
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/reports/" + path)).StatusCode);

        var csv = await admin.GetAsync("/api/v1/reports/export/alerts?format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("AlertId,ClassroomCode", await csv.Content.ReadAsStringAsync());

        var json = await admin.GetAsync("/api/v1/reports/export/alerts");
        Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);

        var future = await admin.GetFromJsonAsync<JsonElement>("/api/v1/reports/alerts?startDate=2999-01-01");
        Assert.Equal(0, future.GetProperty("totalEmergencies").GetInt32());
    }

    [Fact]
    public async Task Preferences_and_school_settings_round_trip()
    {
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        var put = await admin.PutAsJsonAsync("/api/v1/users/me/preferences", new { inAppBannerEnabled = false, alertSoundEnabled = true, pushNotificationsEnabled = true, smsEnabled = true, dailyEmailSummaryEnabled = false });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var prefs = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users/me/preferences");
        Assert.False(prefs.GetProperty("inAppBannerEnabled").GetBoolean());
        Assert.True(prefs.GetProperty("smsEnabled").GetBoolean());

        var school = await admin.GetFromJsonAsync<JsonElement>("/api/v1/schools/current");
        var id = school.GetProperty("id").GetGuid();
        var upd = await admin.PutAsJsonAsync($"/api/v1/schools/{id}", new { name = "Renamed School" });
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        Assert.Equal("Renamed School", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/schools/current")).GetProperty("name").GetString());

        var viewer = await _factory.LoginAsync(DemoSeeder.ViewerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/v1/schools/{id}", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Demo_endpoints_are_not_available_when_demo_mode_is_off()
    {
        var admin = await _factory.LoginAsync(DemoSeeder.AdminEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/demo/simulate-alert", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/demo/reset", null)).StatusCode);
        Assert.False((await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/config")).GetProperty("demoMode").GetBoolean());
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_no_server_internals()
    {
        var res = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(res.Headers.Contains("X-Powered-By"));
    }
}

[Collection("api")]
public class PersistenceTests
{
    [Fact]
    public async Task Incident_history_survives_an_application_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"safeclass-restart-{Guid.NewGuid():N}.db");
        Guid alertId, roomId;

        using (var first = new ApiFactory(path))
        {
            var admin = await first.LoginAsync(DemoSeeder.AdminEmail);
            roomId = await ApiFactory.RoomIdAsync(admin, "B-102");
            var res = await admin.PostAsJsonAsync("/api/v1/alerts", new { classroomId = roomId, alertType = "Medical Assistance" });
            alertId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", null)).StatusCode);
        }

        using (var second = new ApiFactory(path))   // new process state, same database file; seeding is idempotent
        {
            var admin = await second.LoginAsync(DemoSeeder.AdminEmail);
            var alert = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/alerts/{alertId}");
            Assert.Equal("acknowledged", alert.GetProperty("status").GetString());
            var room = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/classrooms/{roomId}");
            Assert.Equal("emergency", room.GetProperty("status").GetString());
            var history = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/alerts/{alertId}/history");
            Assert.True(history.GetArrayLength() >= 2);
            var all = await admin.GetFromJsonAsync<JsonElement>("/api/v1/alerts");
            Assert.Equal(1, all.GetProperty("items").EnumerateArray().Count(a => a.GetProperty("id").GetGuid() == alertId));   // not duplicated by re-seeding
        }

        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { /* best effort */ }
    }
}
