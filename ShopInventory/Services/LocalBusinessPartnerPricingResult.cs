using ShopInventory.DTOs;

namespace ShopInventory.Services;

public sealed class LocalBusinessPartnerPricingResult
{
    public required BusinessPartnerDto BusinessPartner { get; init; }
    public required ItemPricesByListResponseDto Prices { get; init; }
}