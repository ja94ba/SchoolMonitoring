using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;
using SafeClass.Infrastructure.Data;
using SafeClass.Application.Security;
using Xunit;

namespace SafeClass.IntegrationTests;

/// <summary>Runs the real application (real SQLite file, real auth, real SignalR hub) in-process.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "DemoPass!2026";
    public const string DeviceKey = "scd_integration_test_device_key";
    public string DbPath { get; }

    // Settings go through environment variables because Program.cs reads configuration while building.
    // Each factory overwrites them before building its own host; the test collection runs sequentially.
    public ApiFactory(string? dbPath = null, bool demoMode = false)
    {
        DbPath = dbPath ?? Path.Combine(Path.GetTempPath(), $"safeclass-it-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Database__Path", DbPath);
        Environment.SetEnvironmentVariable("Jwt__Key", "integration-test-signing-key-0123456789abcdef");
        Environment.SetEnvironmentVariable("Seed__Enabled", "true");
        Environment.SetEnvironmentVariable("Seed__DemoPassword", Password);
        Environment.SetEnvironmentVariable("Seed__DemoDeviceKey", DeviceKey);
        Environment.SetEnvironmentVariable("SafeClass__DemoModeEnabled", demoMode ? "true" : "false");
        Environment.SetEnvironmentVariable("RateLimiting__LoginPerMinute", "1000");
        Environment.SetEnvironmentVariable("RateLimiting__DevicePerMinute", "1000");
        Environment.SetEnvironmentVariable("Frontend__Path", Path.Combine(Path.GetTempPath(), "no-frontend"));
        using var _ = CreateClient();   // build the host now, while this factory's settings are in place
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task<HttpClient> LoginAsync(string email, string password = Password)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient DeviceClient(string identifier = "ESP32-A101", string key = DeviceKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Identifier", identifier);
        client.DefaultRequestHeaders.Add("X-Device-Key", key);
        return client;
    }

    /// <summary>Adds a second school with its own administrator and classroom directly in the database.</summary>
    public async Task<(string Email, Guid ClassroomId)> AddOtherSchoolAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SafeClassDbContext>();
        var now = DateTime.UtcNow;
        var school = new School { Id = Guid.NewGuid(), SchoolCode = "OTHER-" + Guid.NewGuid().ToString("N")[..6], Name = "Other School", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        var email = $"admin-{Guid.NewGuid():N}@other.test";
        var room = new Classroom { Id = Guid.NewGuid(), SchoolId = school.Id, Code = "Z-900", Name = "Other room", Building = "Z", Floor = "1", Adviser = "n/a", CurrentStatus = ClassroomStatus.Normal, LastStatusUpdateUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now, IsActive = true };
        db.Schools.Add(school);
        db.Users.Add(new User { Id = Guid.NewGuid(), SchoolId = school.Id, FullName = "Other Admin", Email = email, Role = UserRole.Administrator, IsActive = true, PasswordHash = PasswordHasher.Hash(Password), CreatedAtUtc = now, UpdatedAtUtc = now });
        db.Classrooms.Add(room);
        await db.SaveChangesAsync();
        return (email, room.Id);
    }

    public static async Task<Guid> RoomIdAsync(HttpClient client, string code)
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/classrooms?search={Uri.EscapeDataString(code)}&pageSize=50");
        foreach (var item in page.GetProperty("items").EnumerateArray())
            if (item.GetProperty("code").GetString() == code) return item.GetProperty("id").GetGuid();
        throw new InvalidOperationException("Classroom not found: " + code);
    }
}

[CollectionDefinition("api", DisableParallelization = true)]
public sealed class ApiCollection;
