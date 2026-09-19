namespace ShopInventory.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;

/// <summary>The body of <c>PUT api/daily-incoming-payments/gl-mappings/{cardCode}</c>.</summary>
public sealed record SaveIncomingPaymentGlMappingRequest(
    string? CardName,
    string CashAccount,
    string ElectronicAccount,
    string Run,
    string? NotifyEmails,
    bool IsActive);
