using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>
/// Reads and writes the van sales report email schedules.
/// </summary>
public interface IVanReportEmailScheduleService
{
    Task<List<VanReportEmailSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default);

    Task<VanReportEmailSchedule?> GetScheduleAsync(int id, CancellationToken cancellationToken = default);

    Task<VanReportEmailSchedule> SaveScheduleAsync(
        VanReportEmailSchedule schedule,
        string? modifiedBy = null,
        CancellationToken cancellationToken = default);

    Task DeleteScheduleAsync(int id, string? modifiedBy = null, CancellationToken cancellationToken = default);

    Task RecordSendAsync(
        int id,
        bool success,
        string? error,
        CancellationToken cancellationToken = default);
}

public sealed class VanReportEmailScheduleService(
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    ILogger<VanReportEmailScheduleService> logger) : IVanReportEmailScheduleService
{
    /// <summary>Longest error kept on a row. The full text is always in the log.</summary>
    private const int MaxErrorLength = 500;

    public async Task<List<VanReportEmailSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.VanReportEmailSchedules
            .OrderBy(schedule => schedule.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<VanReportEmailSchedule?> GetScheduleAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.VanReportEmailSchedules
            .FirstOrDefaultAsync(schedule => schedule.Id == id, cancellationToken);
    }

    /// <summary>
    /// Creates or updates a schedule.
    /// </summary>
    /// <remarks>
    /// A new schedule is anchored to now, and that anchor is what stops it firing immediately for a
    /// period it was never meant to cover — the one mistake here that would mail every recipient the
    /// moment somebody pressed save.
    ///
    /// An edit deliberately leaves <c>AnchorDateUtc</c> and <c>LastSentUtc</c> alone. Re-anchoring on
    /// every save would let a schedule that is edited often never quite come due, and clearing the
    /// last send would make it send again immediately.
    /// </remarks>
    public async Task<VanReportEmailSchedule> SaveScheduleAsync(
        VanReportEmailSchedule schedule,
        string? modifiedBy = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var nowUtc = DateTime.UtcNow;

        if (schedule.Id == 0)
        {
            schedule.CreatedAtUtc = nowUtc;
            schedule.CreatedBy = modifiedBy;
            schedule.LastModifiedAtUtc = nowUtc;
            schedule.LastModifiedBy = modifiedBy;

            if (schedule.AnchorDateUtc == default)
            {
                schedule.AnchorDateUtc = nowUtc;
            }

            db.VanReportEmailSchedules.Add(schedule);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Van report email schedule {Name} (#{Id}) created by {User}",
                schedule.Name, schedule.Id, modifiedBy);

            return schedule;
        }

        var existing = await db.VanReportEmailSchedules
                .FirstOrDefaultAsync(row => row.Id == schedule.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Van report email schedule {schedule.Id} was not found.");

        existing.Name = schedule.Name;
        existing.Enabled = schedule.Enabled;
        existing.ReportKind = schedule.ReportKind;
        existing.Frequency = schedule.Frequency;
        existing.DayOfWeek = schedule.DayOfWeek;
        existing.DayOfMonth = schedule.DayOfMonth;
        existing.IntervalDays = schedule.IntervalDays;
        existing.SendMinuteOfDay = schedule.SendMinuteOfDay;
        existing.ToRecipients = schedule.ToRecipients;
        existing.CcRecipients = schedule.CcRecipients;
        existing.LastModifiedAtUtc = nowUtc;
        existing.LastModifiedBy = modifiedBy;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Van report email schedule {Name} (#{Id}) updated by {User}",
            existing.Name, existing.Id, modifiedBy);

        return existing;
    }

    public async Task DeleteScheduleAsync(
        int id,
        string? modifiedBy = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.VanReportEmailSchedules
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (existing is null)
        {
            return;
        }

        db.VanReportEmailSchedules.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Van report email schedule {Name} (#{Id}) deleted by {User}",
            existing.Name, id, modifiedBy);
    }

    /// <summary>
    /// Records the outcome of a send.
    /// </summary>
    /// <remarks>
    /// <c>LastSentUtc</c> moves only on success, so a failure stays due and is retried on the next
    /// tick. The error is kept on the row as well as in the log: a report that has quietly stopped
    /// arriving is the failure nobody reports, because the recipients assume there was nothing to
    /// send.
    /// </remarks>
    public async Task RecordSendAsync(
        int id,
        bool success,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.VanReportEmailSchedules
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (existing is null)
        {
            return;
        }

        if (success)
        {
            existing.LastSentUtc = DateTime.UtcNow;
            existing.LastError = null;
        }
        else
        {
            existing.LastError = error is null || error.Length <= MaxErrorLength
                ? error
                : error[..MaxErrorLength];
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
