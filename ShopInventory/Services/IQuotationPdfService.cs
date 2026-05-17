using ShopInventory.DTOs;

namespace ShopInventory.Services;

public interface IQuotationPdfService
{
    Task<byte[]> GenerateQuotationPdfAsync(
        QuotationDto quotation,
        string? customerVatNo = null,
        string? customerTinNumber = null,
        string? customerPhone = null,
        string? customerEmail = null);
}