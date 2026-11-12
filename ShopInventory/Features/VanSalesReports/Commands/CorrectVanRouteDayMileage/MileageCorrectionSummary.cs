using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.CorrectVanRouteDayMileage;

/// <summary>The one place a day's correction trail becomes what a report shows about it.</summary>
public static class MileageCorrectionSummary
{
    /// <summary>
    /// What the corrections changed, against the readings the day holds now. Null when there are none.
    /// </summary>
    /// <remarks>
    /// The rep's own reading is the one before the first correction that changed it. Not simply the
    /// first correction's: an Admin can fix the morning reading on an open day, and the rep's evening
    /// reading arrives only afterwards, so the first correction saw no closing reading at all.
    /// A reading put back to what the rep typed is no longer counted as corrected.
    /// </remarks>
    public static MileageCorrectionSummaryDto? From(
        IEnumerable<VanRouteDayMileageCorrectionEntity> corrections,
        int? startingMileage,
        int? closingMileage)
    {
        var trail = corrections
            .OrderBy(correction => correction.CorrectedAt)
            .ThenBy(correction => correction.Id)
            .ToList();

        if (trail.Count == 0)
        {
            return null;
        }

        var firstStarting = trail.FirstOrDefault(correction => correction.ChangedStartingMileage);
        var firstClosing = trail.FirstOrDefault(correction => correction.ChangedClosingMileage);
        var latest = trail[^1];

        return new MileageCorrectionSummaryDto(
            StartingCorrected: firstStarting is not null && firstStarting.PreviousStartingMileage != startingMileage,
            RecordedStartingMileage: firstStarting?.PreviousStartingMileage ?? startingMileage,
            ClosingCorrected: firstClosing is not null && firstClosing.PreviousClosingMileage != closingMileage,
            RecordedClosingMileage: firstClosing?.PreviousClosingMileage ?? closingMileage,
            CorrectedAt: AuditService.ToCAT(latest.CorrectedAt),
            CorrectedBy: latest.CorrectedByName,
            Reason: latest.Reason,
            Corrections: trail.Count);
    }
}
