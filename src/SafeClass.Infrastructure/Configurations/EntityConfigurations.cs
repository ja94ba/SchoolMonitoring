using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SafeClass.Domain.Entities;

namespace SafeClass.Infrastructure.Configurations;

// All foreign keys use Restrict: deleting a school, classroom, device or alert can never silently cascade
// away its history. Enums are stored as readable text.

internal sealed class SchoolConfiguration : IEntityTypeConfiguration<School>
{
    public void Configure(EntityTypeBuilder<School> b)
    {
        b.ToTable("Schools");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.SchoolCode).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.SchoolCode).IsUnique();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Address).HasMaxLength(200);
        b.Property(x => x.City).HasMaxLength(80);
        b.Property(x => x.Province).HasMaxLength(80);
        b.Property(x => x.Country).HasMaxLength(80);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FullName).HasMaxLength(120).IsRequired();
        b.Property(x => x.Email).HasMaxLength(254).IsRequired();
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.PasswordHash).HasMaxLength(200);
        b.HasIndex(x => x.SchoolId);
        b.HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ClassroomConfiguration : IEntityTypeConfiguration<Classroom>
{
    public void Configure(EntityTypeBuilder<Classroom> b)
    {
        b.ToTable("Classrooms");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Building).HasMaxLength(60).IsRequired();
        b.Property(x => x.Floor).HasMaxLength(40).IsRequired();
        b.Property(x => x.Adviser).HasMaxLength(100).IsRequired();
        b.Property(x => x.CurrentStatus).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.SchoolId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.SchoolId, x.CurrentStatus });
        b.HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Devices).WithOne(d => d.Classroom).HasForeignKey(d => d.ClassroomId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("Devices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DeviceIdentifier).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.DeviceIdentifier).IsUnique();
        b.Property(x => x.DeviceName).HasMaxLength(100).IsRequired();
        b.Property(x => x.DeviceType).HasMaxLength(40).IsRequired();
        b.Property(x => x.FirmwareVersion).HasMaxLength(40);
        b.Property(x => x.ApiKeyHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.SchoolId, x.IsOnline });
        b.HasIndex(x => x.ClassroomId);
        b.HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmergencyAlertConfiguration : IEntityTypeConfiguration<EmergencyAlert>
{
    public void Configure(EntityTypeBuilder<EmergencyAlert> b)
    {
        b.ToTable("EmergencyAlerts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.AlertType).HasMaxLength(64).IsRequired();
        // Status is a concurrency token: two people acknowledging/resolving at once cannot both win.
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken();
        b.Property(x => x.ResolutionNotes).HasMaxLength(500);
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.DeviceEventId).HasMaxLength(100);

        b.HasIndex(x => new { x.SchoolId, x.ReceivedAtUtc });
        b.HasIndex(x => new { x.ClassroomId, x.ReceivedAtUtc });
        b.HasIndex(x => x.Status);
        // Idempotency for ESP32 retries: one alert per (device, device event id).
        b.HasIndex(x => new { x.DeviceId, x.DeviceEventId }).IsUnique().HasFilter("\"DeviceEventId\" IS NOT NULL");

        b.HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Classroom).WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.AcknowledgedByUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ResolvedByUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ClassroomStatusHistoryConfiguration : IEntityTypeConfiguration<ClassroomStatusHistory>
{
    public void Configure(EntityTypeBuilder<ClassroomStatusHistory> b)
    {
        b.ToTable("ClassroomStatusHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.NewStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.TriggerSource).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasIndex(x => new { x.ClassroomId, x.ChangedAtUtc });
        b.HasOne(x => x.Classroom).WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.TriggeredByUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EmergencyAlert>().WithMany().HasForeignKey(x => x.RelatedAlertId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeviceHeartbeatHistoryConfiguration : IEntityTypeConfiguration<DeviceHeartbeatHistory>
{
    public void Configure(EntityTypeBuilder<DeviceHeartbeatHistory> b)
    {
        b.ToTable("DeviceHeartbeatHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.ReportedStatus).HasMaxLength(30);
        b.Property(x => x.FirmwareVersion).HasMaxLength(40);
        b.HasIndex(x => new { x.DeviceId, x.ReceivedAtUtc });
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeviceConnectionHistoryConfiguration : IEntityTypeConfiguration<DeviceConnectionHistory>
{
    public void Configure(EntityTypeBuilder<DeviceConnectionHistory> b)
    {
        b.ToTable("DeviceConnectionHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(200);
        b.HasIndex(x => new { x.DeviceId, x.ChangedAtUtc });
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AlertEventHistoryConfiguration : IEntityTypeConfiguration<AlertEventHistory>
{
    public void Configure(EntityTypeBuilder<AlertEventHistory> b)
    {
        b.ToTable("AlertEventHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        b.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.NewStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => new { x.AlertId, x.EventTimestampUtc });
        b.HasOne<EmergencyAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.PerformedByUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActivityLogConfiguration : IEntityTypeConfiguration<ActivityLog>
{
    public void Configure(EntityTypeBuilder<ActivityLog> b)
    {
        b.ToTable("ActivityLogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.SchoolId, x.CreatedAtUtc });
        b.HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Classroom>().WithMany().HasForeignKey(x => x.ClassroomId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EmergencyAlert>().WithMany().HasForeignKey(x => x.AlertId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.ToTable("NotificationPreferences");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(60).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.Details).HasMaxLength(400);
        b.HasIndex(x => new { x.SchoolId, x.TimestampUtc });
        b.HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}
