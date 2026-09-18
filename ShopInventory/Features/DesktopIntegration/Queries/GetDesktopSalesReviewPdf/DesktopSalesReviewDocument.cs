using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;

/// <summary>The rendered review, and the review it was rendered from so a caller can quote it.</summary>
public sealed record DesktopSalesReviewDocument(byte[] Content, string FileName, DesktopSalesReview Review);
