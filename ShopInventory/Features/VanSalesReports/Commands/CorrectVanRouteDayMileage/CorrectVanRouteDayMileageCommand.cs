using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Commands.CorrectVanRouteDayMileage;

/// <summary>
/// An Admin's correction of the odometer readings a rep typed on one trading day.
/// </summary>
/// <remarks>
/// <para>
/// Both readings are sent as they should stand, so a blank clears one: a reading that is known to be
/// wrong and has no right figure to replace it is better absent than counted.
/// </para>
/// <para>
/// <c>ExpectedStartingMileage</c> and <c>ExpectedClosingMileage</c> are what the page showed. They must
/// still be what the day holds, or the save is refused: the commonest way for them not to be is the
/// rep ending the day after the page was loaded, and writing the page's blank over the rep's evening
/// reading would lose it.
/// </para>
/// </remarks>
public sealed record CorrectVanRouteDayMileageCommand(
    int VanRouteDayId,
    int? StartingMileage,
    int? ClosingMileage,
    int? ExpectedStartingMileage,
    int? ExpectedClosingMileage,
    string? Reason,
    Guid? ActingUserId
) : IRequest<ErrorOr<VanRouteDayMileageDto>>;

/// <summary>A trading day's odometer readings as they now stand, and how they came to.</summary>
public sealed record VanRouteDayMileageDto(
    int VanRouteDayId,
    int? StartingMileage,
    int? ClosingMileage,
    int? KilometresTravelled,
    MileageCorrectionSummaryDto? Correction);

/// <summary>
/// What an Admin changed about a day's odometer readings, read from every correction it has had.
/// Null on a day that has never been corrected.
/// </summary>
/// <param name="StartingCorrected">Whether the reading out has been changed from what the rep typed.</param>
/// <param name="RecordedStartingMileage">The reading out the rep typed. Meaningful only when <c>StartingCorrected</c>.</param>
/// <param name="ClosingCorrected">Whether the reading in has been changed from what the rep typed.</param>
/// <param name="RecordedClosingMileage">The reading in the rep typed. Meaningful only when <c>ClosingCorrected</c>.</param>
/// <param name="CorrectedAt">The latest correction, in CAT.</param>
/// <param name="CorrectedBy">Who made the latest correction.</param>
/// <param name="Reason">The latest correction's reason.</param>
/// <param name="Corrections">How many corrections the day has had.</param>
public sealed record MileageCorrectionSummaryDto(
    bool StartingCorrected,
    int? RecordedStartingMileage,
    bool ClosingCorrected,
    int? RecordedClosingMileage,
    DateTime CorrectedAt,
    string? CorrectedBy,
    string Reason,
    int Corrections);
