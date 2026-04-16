namespace ShopInventory.DTOs;

public class StockValidationResultDto
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<StockReservationErrorDto> Errors { get; set; } = new();
}
