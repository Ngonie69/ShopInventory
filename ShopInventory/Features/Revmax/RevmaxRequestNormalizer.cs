using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax;

internal static class RevmaxRequestNormalizer
{
    internal static void ApplyDefaults(
        TransactMRequest request,
        string defaultCurrency,
        string defaultBranchName)
    {
        request.Currency = NormalizeOrFallback(request.Currency, defaultCurrency);
        request.BranchName = NormalizeOrFallback(request.BranchName, defaultBranchName);
        request.OriginalInvoiceNumber = NormalizeOrFallback(request.OriginalInvoiceNumber, string.Empty);
        request.CustomerName = NormalizeOrFallback(request.CustomerName, string.Empty);
        request.CustomerVatNumber = NormalizeOrFallback(request.CustomerVatNumber, string.Empty);
        request.CustomerAddress = NormalizeOrFallback(request.CustomerAddress, string.Empty);
        request.CustomerTelephone = NormalizeOrFallback(request.CustomerTelephone, string.Empty);
        request.CustomerEmail = NormalizeOrFallback(request.CustomerEmail, string.Empty);
        request.CustomerBPN = NormalizeOrFallback(request.CustomerBPN, string.Empty);
        request.Cashier = NormalizeOrFallback(request.Cashier, string.Empty);
        request.InvoiceComment = NormalizeOrFallback(request.InvoiceComment, string.Empty);
    }

    private static string NormalizeOrFallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}