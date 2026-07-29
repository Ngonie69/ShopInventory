namespace ShopInventory.Services;

public sealed class SapPostingPeriodException(
    string message,
    string docDate,
    string? sapError = null) : InvalidOperationException(message)
{
    public string DocDate { get; } = docDate;
    public string? SapError { get; } = sapError;
}