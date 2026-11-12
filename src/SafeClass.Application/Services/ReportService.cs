using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SafeClass.Application.Common;
using SafeClass.Application.DTOs;
using SafeClass.Application.Interfaces;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Application.Services;

/// <summary>
/// Historical reporting. Everything is computed from persisted rows (alerts, events, status and connection
/// history), never from the live dashboard state. Counts and groupings run in SQLite; response-time
/// statistics use a SQL aggregate over the original timestamps. Incomplete incidents (never acknowledged or
/// never resolved) are counted separately and are never treated as zero-duration.
/// </summary>
public sealed class ReportService(ISafeClassDbContext db, ICurrentUser user, TimeProvider time) : IReportService
{
    public sealed class AggRow
    {
        public long Cnt { get; set; }
        public double? AvgSec { get; set; }
        public double? MinSec { get; set; }
        public double? MaxSec { get; set; }
    }

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<AlertReportDto> GetAlertsAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var (page, size) = Paging.Normalize(filter.Page, filter.PageSize);
        var q = AlertQueries.ApplyFilter(db.EmergencyAlerts.AsNoTracking(), filter, schoolId);

        var counts = await q.GroupBy(a => a.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int C(AlertStatus s) => counts.Where(x => x.Status == s).Select(x => x.Count).FirstOrDefault();
        var total = counts.Sum(x => x.Count);

        var rows = await ProjectRows(q).OrderByDescending(r => r.ReceivedAtUtc)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);

        return new AlertReportDto
        {
            TotalEmergencies = total, ActiveCount = C(AlertStatus.Active), AcknowledgedCount = C(AlertStatus.Acknowledged),
            ResolvedCount = C(AlertStatus.Resolved),
            Alerts = new PagedResult<AlertReportRowDto>
            {
                Items = rows.Select(WithDurations).ToList(), Page = page, PageSize = size, TotalCount = total
            }
        };
    }

    public async Task<IReadOnlyList<CountByClassroomDto>> GetByClassroomAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var q = AlertQueries.ApplyFilter(db.EmergencyAlerts.AsNoTracking(), filter, schoolId);
        var grouped = await q.GroupBy(a => a.ClassroomId)
            .Select(g => new { ClassroomId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).Take(500)
            .ToListAsync(ct);

        var ids = grouped.Select(g => g.ClassroomId).ToList();
        var rooms = await db.Classrooms.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Code, c.Name, c.Building }).ToListAsync(ct);

        return grouped.Select(g =>
        {
            var room = rooms.FirstOrDefault(r => r.Id == g.ClassroomId);
            return new CountByClassroomDto
            {
                ClassroomId = g.ClassroomId, Code = room?.Code ?? "", Name = room?.Name ?? "", Building = room?.Building ?? "", Count = g.Count
            };
        }).OrderByDescending(x => x.Count).ThenBy(x => x.Code).ToList();
    }

    public async Task<IReadOnlyList<CountByTypeDto>> GetByTypeAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var q = AlertQueries.ApplyFilter(db.EmergencyAlerts.AsNoTracking(), filter, schoolId);
        var rows = await q.GroupBy(a => a.AlertType)
            .Select(g => new CountByTypeDto { AlertType = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ToListAsync(ct);
        return rows;
    }

    public async Task<ResponseTimeReportDto> GetResponseTimesAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var q = AlertQueries.ApplyFilter(db.EmergencyAlerts.AsNoTracking(), filter, schoolId);

        var total = await q.CountAsync(ct);
        var incompleteAck = await q.CountAsync(a => a.AcknowledgedAtUtc == null, ct);
        var incompleteRes = await q.CountAsync(a => a.ResolvedAtUtc == null, ct);

        var ack = await DurationStatsAsync(filter, schoolId, "AcknowledgedAtUtc", ct);
        var res = await DurationStatsAsync(filter, schoolId, "ResolvedAtUtc", ct);

        // Per-day counts are grouped in SQLite; weeks and months are rolled up from those few rows.
        var daily = await q.GroupBy(a => new { a.ReceivedAtUtc.Year, a.ReceivedAtUtc.Month, a.ReceivedAtUtc.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
            .ToListAsync(ct);
        var days = daily.Select(d => (Date: new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc), d.Count))
            .OrderBy(d => d.Date).ToList();

        var perDay = days.Select(d => new PeriodCountDto { Period = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Count = d.Count }).ToList();
        var perWeek = days.GroupBy(d => IsoWeekStart(d.Date))
            .Select(g => new PeriodCountDto { Period = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Count = g.Sum(x => x.Count) })
            .OrderBy(p => p.Period, StringComparer.Ordinal).ToList();
        var perMonth = days.GroupBy(d => d.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .Select(g => new PeriodCountDto { Period = g.Key, Count = g.Sum(x => x.Count) })
            .OrderBy(p => p.Period, StringComparer.Ordinal).ToList();

        return new ResponseTimeReportDto
        {
            TotalIncidents = total, Acknowledgement = ack, Resolution = res,
            IncompleteAcknowledgement = incompleteAck, IncompleteResolution = incompleteRes,
            PerDay = perDay, PerWeek = perWeek, PerMonth = perMonth
        };
    }

    public async Task<PagedResult<ClassroomHistoryReportRowDto>> GetClassroomHistoryAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var (page, size) = Paging.Normalize(filter.Page, filter.PageSize);

        var q = db.ClassroomStatusHistory.AsNoTracking().Where(h => h.Classroom!.SchoolId == schoolId);
        if (filter.ClassroomId is Guid classroomId) q = q.Where(h => h.ClassroomId == classroomId);
        if (!string.IsNullOrWhiteSpace(filter.Building))
        {
            var building = filter.Building.Trim();
            q = q.Where(h => h.Classroom!.Building == building);
        }
        var (from, toEx) = ServiceHelpers.DateRange(filter.StartDate, filter.EndDate);
        if (from is DateTime fr) q = q.Where(h => h.ChangedAtUtc >= fr);
        if (toEx is DateTime te) q = q.Where(h => h.ChangedAtUtc < te);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(h => new ClassroomHistoryReportRowDto
            {
                Id = h.Id, ClassroomId = h.ClassroomId, ClassroomCode = h.Classroom!.Code, Building = h.Classroom.Building,
                PreviousStatus = h.PreviousStatus, NewStatus = h.NewStatus, ChangedAtUtc = h.ChangedAtUtc,
                TriggerSource = h.TriggerSource, RelatedAlertId = h.RelatedAlertId, Reason = h.Reason
            })
            .ToListAsync(ct);
        return new PagedResult<ClassroomHistoryReportRowDto> { Items = rows, Page = page, PageSize = size, TotalCount = total };
    }

    public async Task<IReadOnlyList<DeviceUptimeDto>> GetDeviceUptimeAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var now = time.GetUtcNow().UtcDateTime;
        var (from, toEx) = ServiceHelpers.DateRange(filter.StartDate, filter.EndDate);
        var periodEnd = toEx.HasValue && toEx.Value < now ? toEx.Value : now;

        var dq = db.Devices.AsNoTracking().Where(d => d.SchoolId == schoolId);
        if (filter.ClassroomId is Guid classroomId) dq = dq.Where(d => d.ClassroomId == classroomId);
        if (!string.IsNullOrWhiteSpace(filter.Building))
        {
            var building = filter.Building.Trim();
            dq = dq.Where(d => d.Classroom!.Building == building);
        }
        var devices = await dq.Select(d => new
        {
            d.Id, d.DeviceIdentifier, d.IsOnline, d.RegisteredAtUtc, Code = d.Classroom!.Code, Building = d.Classroom.Building
        }).ToListAsync(ct);
        var ids = devices.Select(d => d.Id).ToList();

        // Connection events are transitions only (a handful per outage), so loading them is cheap.
        var eq = db.DeviceConnectionHistory.AsNoTracking().Where(e => ids.Contains(e.DeviceId));
        if (toEx is DateTime te) eq = eq.Where(e => e.ChangedAtUtc < te);
        var events = await eq.OrderBy(e => e.ChangedAtUtc).ThenBy(e => e.Id)
            .Select(e => new { e.DeviceId, e.IsOnline, e.ChangedAtUtc }).ToListAsync(ct);

        var result = new List<DeviceUptimeDto>();
        foreach (var d in devices)
        {
            var periodStart = from ?? d.RegisteredAtUtc;
            if (periodStart < d.RegisteredAtUtc) periodStart = d.RegisteredAtUtc;
            var totalSeconds = Math.Max(0, (periodEnd - periodStart).TotalSeconds);

            // Reconstruct offline intervals, clipped to [periodStart, periodEnd].
            var offlineSince = (DateTime?)null;
            var outages = new List<double>();
            foreach (var e in events.Where(x => x.DeviceId == d.Id))
            {
                if (!e.IsOnline && offlineSince is null) offlineSince = e.ChangedAtUtc;
                else if (e.IsOnline && offlineSince is not null)
                {
                    AddOutage(outages, offlineSince.Value, e.ChangedAtUtc, periodStart, periodEnd);
                    offlineSince = null;
                }
            }
            if (offlineSince is not null) AddOutage(outages, offlineSince.Value, periodEnd, periodStart, periodEnd);

            var offline = outages.Sum();
            result.Add(new DeviceUptimeDto
            {
                DeviceId = d.Id, DeviceIdentifier = d.DeviceIdentifier, ClassroomCode = d.Code, Building = d.Building,
                CurrentlyOnline = d.IsOnline, OutageCount = outages.Count, TotalOfflineSeconds = Math.Round(offline, 1),
                LongestOutageSeconds = Math.Round(outages.Count == 0 ? 0 : outages.Max(), 1),
                UptimePercent = totalSeconds <= 0 ? 100 : Math.Round(Math.Max(0, 100 * (1 - offline / totalSeconds)), 2)
            });
        }
        return result.OrderBy(r => r.UptimePercent).ThenBy(r => r.DeviceIdentifier, StringComparer.Ordinal).ToList();
    }

    public async Task<ReportDashboardDto> GetDashboardAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var byClassroom = await GetByClassroomAsync(filter, ct);
        var byType = await GetByTypeAsync(filter, ct);
        var response = await GetResponseTimesAsync(filter, ct);
        var uptime = await GetDeviceUptimeAsync(filter, ct);
        return new ReportDashboardDto
        {
            TotalEmergencies = response.TotalIncidents,
            TopClassrooms = byClassroom.Take(5).ToList(),
            ByType = byType,
            ResponseTimes = response,
            DeviceUptime = uptime
        };
    }

    public async Task ExportAlertsAsync(AlertFilter filter, bool csv, Stream output, CancellationToken ct = default)
    {
        var schoolId = user.ResolveSchool(filter.SchoolId);
        var q = AlertQueries.ApplyFilter(db.EmergencyAlerts.AsNoTracking(), filter, schoolId);
        var rows = ProjectRows(q).OrderBy(r => r.ReceivedAtUtc).AsAsyncEnumerable();

        if (csv)
        {
            await using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true);
            await writer.WriteLineAsync("AlertId,ClassroomCode,ClassroomName,Building,AlertType,Status,ReceivedAtUtc,AcknowledgedAtUtc,ResolvedAtUtc,AcknowledgementSeconds,ResolutionSeconds,Source,ResolutionNotes");
            await foreach (var r in rows.WithCancellation(ct))
            {
                var x = WithDurations(r);
                await writer.WriteLineAsync(string.Join(',',
                    Csv(x.Id.ToString()), Csv(x.ClassroomCode), Csv(x.ClassroomName), Csv(x.Building), Csv(x.AlertType),
                    Csv(x.Status.ToString().ToLowerInvariant()), Csv(Iso(x.ReceivedAtUtc)), Csv(Iso(x.AcknowledgedAtUtc)),
                    Csv(Iso(x.ResolvedAtUtc)), Csv(Num(x.AcknowledgementSeconds)), Csv(Num(x.ResolutionSeconds)),
                    Csv(x.Source), Csv(x.ResolutionNotes)));
            }
            await writer.FlushAsync(ct);
        }
        else
        {
            await using var json = new Utf8JsonWriter(output);
            json.WriteStartArray();
            await foreach (var r in rows.WithCancellation(ct))
            {
                JsonSerializer.Serialize(json, WithDurations(r), ExportJson);
            }
            json.WriteEndArray();
            await json.FlushAsync(ct);
        }
    }

    // ---------------------------------------------------------------- helpers

    private IQueryable<AlertReportRow> ProjectRows(IQueryable<EmergencyAlert> q) =>
        q.Select(a => new AlertReportRow
        {
            Id = a.Id, ClassroomId = a.ClassroomId, ClassroomCode = a.Classroom!.Code, ClassroomName = a.Classroom.Name,
            Building = a.Classroom.Building, AlertType = a.AlertType, Status = a.Status, ReceivedAtUtc = a.ReceivedAtUtc,
            AcknowledgedAtUtc = a.AcknowledgedAtUtc, ResolvedAtUtc = a.ResolvedAtUtc, Source = a.Source,
            ResolutionNotes = a.ResolutionNotes
        });

    private sealed class AlertReportRow
    {
        public Guid Id { get; set; }
        public Guid ClassroomId { get; set; }
        public string ClassroomCode { get; set; } = "";
        public string ClassroomName { get; set; } = "";
        public string Building { get; set; } = "";
        public string AlertType { get; set; } = "";
        public AlertStatus Status { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
        public DateTime? AcknowledgedAtUtc { get; set; }
        public DateTime? ResolvedAtUtc { get; set; }
        public string Source { get; set; } = "";
        public string? ResolutionNotes { get; set; }
    }

    private static AlertReportRowDto WithDurations(AlertReportRow r) => new()
    {
        Id = r.Id, ClassroomId = r.ClassroomId, ClassroomCode = r.ClassroomCode, ClassroomName = r.ClassroomName,
        Building = r.Building, AlertType = r.AlertType, Status = r.Status,
        ReceivedAtUtc = r.ReceivedAtUtc.AsUtc(), AcknowledgedAtUtc = r.AcknowledgedAtUtc?.AsUtc(), ResolvedAtUtc = r.ResolvedAtUtc?.AsUtc(),
        // AcknowledgedAtUtc - ReceivedAtUtc and ResolvedAtUtc - ReceivedAtUtc; null (not zero) when incomplete.
        AcknowledgementSeconds = r.AcknowledgedAtUtc.HasValue ? (r.AcknowledgedAtUtc.Value - r.ReceivedAtUtc).TotalSeconds : null,
        ResolutionSeconds = r.ResolvedAtUtc.HasValue ? (r.ResolvedAtUtc.Value - r.ReceivedAtUtc).TotalSeconds : null,
        Source = r.Source, ResolutionNotes = r.ResolutionNotes
    };

    /// <summary>SQL aggregate over the original persisted timestamps. Column is one of two fixed names, never user input.</summary>
    private async Task<DurationStatsDto> DurationStatsAsync(AlertFilter f, Guid schoolId, string milestoneColumn, CancellationToken ct)
    {
        if (milestoneColumn is not ("AcknowledgedAtUtc" or "ResolvedAtUtc")) throw new ArgumentException("Invalid column.", nameof(milestoneColumn));

        var args = new List<object>();
        string P(object v) { args.Add(v); return "{" + (args.Count - 1) + "}"; }

        var where = new StringBuilder($"a.SchoolId = {P(schoolId.ToString().ToUpperInvariant())} AND a.{milestoneColumn} IS NOT NULL");
        if (f.ClassroomId is Guid classroomId) where.Append($" AND a.ClassroomId = {P(classroomId.ToString().ToUpperInvariant())}");
        if (!string.IsNullOrWhiteSpace(f.Building)) where.Append($" AND c.Building = {P(f.Building.Trim())}");
        if (!string.IsNullOrWhiteSpace(f.AlertType)) where.Append($" AND a.AlertType = {P(f.AlertType.Trim())}");
        if (f.Status is AlertStatus status) where.Append($" AND a.Status = {P(status.ToString())}");
        var (from, toEx) = ServiceHelpers.DateRange(f.StartDate, f.EndDate);
        if (from is DateTime fr) where.Append($" AND a.ReceivedAtUtc >= {P(SqliteDate(fr))}");
        if (toEx is DateTime te) where.Append($" AND a.ReceivedAtUtc < {P(SqliteDate(te))}");

        var diff = $"(julianday(a.{milestoneColumn}) - julianday(a.ReceivedAtUtc)) * 86400.0";
        var sql = $"SELECT COUNT(*) AS Cnt, AVG({diff}) AS AvgSec, MIN({diff}) AS MinSec, MAX({diff}) AS MaxSec " +
                  $"FROM EmergencyAlerts a JOIN Classrooms c ON c.Id = a.ClassroomId WHERE {where}";

        var row = (await db.Database.SqlQueryRaw<AggRow>(sql, args.ToArray()).ToListAsync(ct)).FirstOrDefault() ?? new AggRow();
        return new DurationStatsDto
        {
            Count = row.Cnt,
            AverageSeconds = row.AvgSec.HasValue ? Math.Round(row.AvgSec.Value, 3) : null,
            FastestSeconds = row.MinSec.HasValue ? Math.Round(row.MinSec.Value, 3) : null,
            SlowestSeconds = row.MaxSec.HasValue ? Math.Round(row.MaxSec.Value, 3) : null
        };
    }

    // SQLite stores DateTime as text in this format; the same format is used for raw comparisons.
    private static string SqliteDate(DateTime dt) => dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    private static void AddOutage(List<double> outages, DateTime start, DateTime end, DateTime periodStart, DateTime periodEnd)
    {
        var s = start < periodStart ? periodStart : start;
        var e = end > periodEnd ? periodEnd : end;
        if (e > s) outages.Add((e - s).TotalSeconds);
    }

    private static DateTime IsoWeekStart(DateTime d)
    {
        var diff = ((int)d.DayOfWeek + 6) % 7; // Monday = 0
        return d.Date.AddDays(-diff);
    }

    private static string Iso(DateTime? dt) => dt.HasValue ? dt.Value.AsUtc().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) : "";
    private static string Num(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";

    /// <summary>RFC 4180 quoting, plus a leading apostrophe on cells that spreadsheets could run as formulas.</summary>
    private static string Csv(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        if (v.IndexOfAny([',', '"', '\n', '\r']) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
