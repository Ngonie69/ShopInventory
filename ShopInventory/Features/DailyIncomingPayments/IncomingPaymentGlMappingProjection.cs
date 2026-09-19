using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DailyIncomingPayments;

internal static class IncomingPaymentGlMappingProjection
{
    public static IncomingPaymentGlMappingDto ToDto(IncomingPaymentGlMappingEntity mapping) =>
        new(
            mapping.CardCode,
            mapping.CardName,
            mapping.CashAccount,
            mapping.ElectronicAccount,
            mapping.Run.ToString(),
            mapping.NotifyEmails,
            mapping.NeedsReview,
            mapping.IsActive,
            mapping.UpdatedAtUtc,
            mapping.UpdatedBy);
}
