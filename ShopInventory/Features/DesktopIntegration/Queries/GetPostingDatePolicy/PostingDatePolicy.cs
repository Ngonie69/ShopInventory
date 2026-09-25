namespace ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;

/// <summary>Whether a till may choose the date its sale is posted to SAP under.</summary>
/// <param name="AllowCustomPostingDate">
/// Off, every till sale posts on the day it was rung up. On, the till shows a date picker and the sale's
/// SAP invoice takes the chosen day as its posting, due and document date.
/// </param>
/// <param name="TodayCat">The business day the platform counts as today, so a till never argues with it.</param>
/// <param name="UpdatedAtUtc">When an admin last changed it; null if nobody ever has.</param>
/// <param name="UpdatedBy">Who that was.</param>
public sealed record PostingDatePolicy(
    bool AllowCustomPostingDate,
    DateTime TodayCat,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);
