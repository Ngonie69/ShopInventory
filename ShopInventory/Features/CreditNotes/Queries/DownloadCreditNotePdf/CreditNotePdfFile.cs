namespace ShopInventory.Features.CreditNotes.Queries.DownloadCreditNotePdf;

public sealed record CreditNotePdfFile(byte[] PdfBytes, string FileName);
