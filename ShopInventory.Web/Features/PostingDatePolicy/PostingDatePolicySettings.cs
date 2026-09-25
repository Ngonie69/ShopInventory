namespace ShopInventory.Web.Features.PostingDatePolicy;

/// <summary>Whether a till may choose the date its sale is posted to SAP under, as the API reports it.</summary>
public sealed record PostingDatePolicySettings(
    bool AllowCustomPostingDate,
    DateTime TodayCat,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);
