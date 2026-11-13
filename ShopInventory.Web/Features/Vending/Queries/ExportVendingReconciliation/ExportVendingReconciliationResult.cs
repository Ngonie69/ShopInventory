namespace ShopInventory.Web.Features.Vending.Queries.ExportVendingReconciliation;

public sealed record ExportVendingReconciliationResult(string FileName, byte[] Content);
