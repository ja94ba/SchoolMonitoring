using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SafeClass.Application.DTOs;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Options;
using SafeClass.Application.Security;
using SafeClass.Application.Services;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;
using SafeClass.Infrastructure.Data;

namespace SafeClass.UnitTests;

public sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeUser : ICurrentUser
{
    public bool IsAuthenticated { get; set; } = true;
    public Guid? UserId { get; set; }
    public Guid? SchoolId { get; set; }
    public UserRole? Role { get; set; }
    public bool IsDevice { get; set; }
    public Guid? DeviceId { get; set; }
}

public sealed class RecordingNotifier : IMonitoringNotifier
{
    public List<string> Events { get; } = [];
    public bool Throw { get; set; }
    private Task Add(string e) { if (Throw) throw new InvalidOperationException("hub down"); Events.Add(e); return Task.CompletedTask; }
    public Task ClassroomStatusChangedAsync(Guid s, ClassroomDto c, CancellationToken ct = default) => Add("status:" + c.Code + ":" + c.Status);
    public Task AlertCreatedAsync(Guid s, AlertDto a, CancellationToken ct = default) => Add("created:" + a.RoomCode);
    public Task AlertAcknowledgedAsync(Guid s, AlertDto a, CancellationToken ct = default) => Add("ack:" + a.RoomCode);
    public Task AlertResolvedAsync(Guid s, AlertDto a, CancellationToken ct = default) => Add("resolved:" + a.RoomCode);
    public Task DeviceConnectionChangedAsync(Guid s, DeviceDto d, CancellationToken ct = default) => Add("device:" + d.DeviceIdentifier + ":" + d.IsOnline);
}

/// <summary>
/// Real SQLite (in-memory connection, foreign keys on) with two schools. Services are built exactly as in production,
/// only the clock, caller and notifier are fakes.
/// </summary>
public sealed class Harness : IDisposable
{
    public const string DeviceKey = "scd_test_key_0123456789";
    private readonly SqliteConnection _conn;

    public FakeTime Time { get; } = new();
    public FakeUser User { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public SafeClassOptions Options { get; } = new() { HeartbeatOfflineThresholdSeconds = 60, HeartbeatHistoryMinIntervalSeconds = 60, DemoModeEnabled = true };

    public School SchoolA { get; private set; } = null!;
    public School SchoolB { get; private set; } = null!;
    public User Responder { get; private set; } = null!;
    public User Viewer { get; private set; } = null!;
    public User OtherSchoolAdmin { get; private set; } = null!;
    public Classroom Room1 { get; private set; } = null!;
    public Classroom Room2 { get; private set; } = null!;
    public Classroom OtherRoom { get; private set; } = null!;
    public Device Device1 { get; private set; } = null!;
    public Device Device2 { get; private set; } = null!;
    public Device OtherDevice { get; private set; } = null!;

    public Harness()
    {
        _conn = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        _conn.Open();
        using var db = NewDb();
        db.Database.EnsureCreated();
        Seed(db);
        ActAs(Responder);
    }

    public SafeClassDbContext NewDb() => new(new DbContextOptionsBuilder<SafeClassDbContext>().UseSqlite(_conn).Options);

    private IOptions<SafeClassOptions> Opt => Microsoft.Extensions.Options.Options.Create(Options);

    public void ActAs(User u) { User.UserId = u.Id; User.SchoolId = u.SchoolId; User.Role = u.Role; User.IsDevice = false; User.DeviceId = null; }
    public void ActAsDevice(Device d) { User.UserId = null; User.Role = null; User.SchoolId = d.SchoolId; User.IsDevice = true; User.DeviceId = d.Id; }

    public AlertService Alerts(SafeClassDbContext db) => new(db, User, Notifier, Time, Opt, NullLogger<AlertService>.Instance);
    public DeviceService Devices(SafeClassDbContext db) => new(db, User, Notifier, Time, Opt, NullLogger<DeviceService>.Instance);
    public ClassroomService Classrooms(SafeClassDbContext db) => new(db, User, Time);
    public DashboardService Dashboard(SafeClassDbContext db) => new(db, User);
    public ReportService Reports(SafeClassDbContext db) => new(db, User, Time);

    private void Seed(SafeClassDbContext db)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        School NewSchool(string code, string name) => new() { Id = Guid.NewGuid(), SchoolCode = code, Name = name, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        User NewUser(School s, string name, string email, UserRole role) => new()
        { Id = Guid.NewGuid(), SchoolId = s.Id, FullName = name, Email = email, Role = role, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now, PasswordHash = "x" };
        Classroom NewRoom(School s, string code) => new()
        { Id = Guid.NewGuid(), SchoolId = s.Id, Code = code, Name = "Room " + code, Building = "Building A", Floor = "1st floor", Adviser = "Adviser",
          CurrentStatus = ClassroomStatus.Normal, LastStatusUpdateUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now, IsActive = true };
        Device NewDevice(School s, Classroom r, string id) => new()
        { Id = Guid.NewGuid(), SchoolId = s.Id, ClassroomId = r.Id, DeviceIdentifier = id, DeviceName = id, SignalStrength = 4, IsOnline = true,
          LastHeartbeatUtc = now, RegisteredAtUtc = now, LastUpdatedUtc = now, IsActive = true, ApiKeyHash = DeviceCredentials.Hash(DeviceKey) };

        SchoolA = NewSchool("SCH-A", "School A"); SchoolB = NewSchool("SCH-B", "School B");
        Responder = NewUser(SchoolA, "Rita Responder", "rita@a.test", UserRole.SafetyCoordinator);
        Viewer = NewUser(SchoolA, "Vic Viewer", "vic@a.test", UserRole.Viewer);
        OtherSchoolAdmin = NewUser(SchoolB, "Olga Other", "olga@b.test", UserRole.Administrator);
        Room1 = NewRoom(SchoolA, "A-101"); Room2 = NewRoom(SchoolA, "A-102"); OtherRoom = NewRoom(SchoolB, "B-101");
        Device1 = NewDevice(SchoolA, Room1, "ESP32-A101"); Device2 = NewDevice(SchoolA, Room2, "ESP32-A102"); OtherDevice = NewDevice(SchoolB, OtherRoom, "ESP32-B101");

        db.Schools.AddRange(SchoolA, SchoolB);
        db.Users.AddRange(Responder, Viewer, OtherSchoolAdmin);
        db.Classrooms.AddRange(Room1, Room2, OtherRoom);
        db.Devices.AddRange(Device1, Device2, OtherDevice);
        db.SaveChanges();
    }

    public void Dispose() => _conn.Dispose();
}
