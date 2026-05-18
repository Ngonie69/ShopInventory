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
    Task<LocalBusinessPartnerPricingResult?> GetBusinessPartnerPricingAsync(
        string cardCode,
        IReadOnlyCollection<string>? itemCodes = null,
        CancellationToken cancellationToken = default);
}