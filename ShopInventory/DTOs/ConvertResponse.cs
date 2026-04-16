namespace ShopInventory.DTOs;

public class ConvertResponse
{
    public decimal Amount { get; set; }
    public string FromCurrency { get; set; } = null!;
    public string ToCurrency { get; set; } = null!;
    public decimal ConvertedAmount { get; set; }
    public decimal Rate { get; set; }
}
