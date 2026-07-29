using ShopInventory.DTOs;

namespace ShopInventory.Services;

public interface ILocalPriceCatalogService
{
    Task<PriceListsResponseDto> GetPriceListsAsync(CancellationToken cancellationToken = default);
    Task<ItemPricesResponseDto> GetAllPricesAsync(CancellationToken cancellationToken = default);
    Task<ItemPricesGroupedResponseDto> GetGroupedPricesAsync(CancellationToken cancellationToken = default);
    Task<ItemPriceGroupedDto?> GetGroupedPriceByItemCodeAsync(string itemCode, CancellationToken cancellationToken = default);
    Task<ItemPricesResponseDto> GetPricesByCurrencyAsync(string currency, CancellationToken cancellationToken = default);
    Task<ItemPricesByListResponseDto> GetPricesByPriceListAsync(
        int priceListNum,
        IReadOnlyCollection<string>? itemCodes = null,
        CancellationToken cancellationToken = default);
    Task<ItemPriceByListDto?> GetItemPriceFromListAsync(
        int priceListNum,
        string itemCode,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// The business partner's special prices that apply today, from the locally synced catalog.
    /// </summary>
    /// <remarks>
    /// Special prices are negotiated for long validity windows and change rarely, so the synced
    /// copy is a sound answer when SAP cannot be reached — and a far better one than falling back
    /// to list price, which overcharges the customer.
    /// </remarks>
    Task<Dictionary<string, decimal>> GetActiveSpecialPricesAsync(
        string cardCode,
        IReadOnlyCollection<string>? itemCodes = null,
        CancellationToken cancellationToken = default);

    Task<LocalBusinessPartnerPricingResult?> GetBusinessPartnerPricingAsync(
        string cardCode,
        IReadOnlyCollection<string>? itemCodes = null,
        CancellationToken cancellationToken = default);
}