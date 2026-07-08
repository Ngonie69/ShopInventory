using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

public interface IPodReportEmailScheduleService
{
    Task<List<PodReportEmailSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default);
    Task<PodReportEmailSchedule?> GetScheduleAsync(int id, CancellationToken cancellationToken = default);
    Task<PodReportEmailSchedule> SaveScheduleAsync(PodReportEmailSchedule schedule, string? modifiedBy = null, CancellationToken cancellationToken = default);
    Task DeleteScheduleAsync(int id, string? modifiedBy = null, CancellationToken cancellationToken = default);
    Task MarkSentAsync(int id, DateTime sentUtc, CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

public sealed class PodReportEmailScheduleService(
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    ILogger<PodReportEmailScheduleService> logger) : IPodReportEmailScheduleService
{
    public async Task<List<PodReportEmailSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PodReportEmailSchedules
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<PodReportEmailSchedule?> GetScheduleAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PodReportEmailSchedules.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<PodReportEmailSchedule> SaveScheduleAsync(
        PodReportEmailSchedule schedule,
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

            db.PodReportEmailSchedules.Add(schedule);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "POD report email schedule {Name} (#{Id}) created by {User}",
                schedule.Name,
                schedule.Id,
                modifiedBy);
            return schedule;
        }

        var existing = await db.PodReportEmailSchedules.FirstOrDefaultAsync(s => s.Id == schedule.Id, cancellationToken)
            ?? throw new InvalidOperationException($"POD report email schedule {schedule.Id} was not found.");

        existing.Name = schedule.Name;
        existing.Enabled = schedule.Enabled;
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
            "POD report email schedule {Name} (#{Id}) updated by {User}",
            existing.Name,
            existing.Id,
            modifiedBy);
        return existing;
    }

    public async Task DeleteScheduleAsync(int id, string? modifiedBy = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PodReportEmailSchedules.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (existing is null)
        {
            return;
        }

        db.PodReportEmailSchedules.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "POD report email schedule {Name} (#{Id}) deleted by {User}",
            existing.Name,
            id,
            modifiedBy);
    }

    public async Task MarkSentAsync(int id, DateTime sentUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PodReportEmailSchedules.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (existing is null)
        {
            return;
        }

        existing.LastSentUtc = sentUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PodReportEmailSchedules.CountAsync(cancellationToken);
    }
}
