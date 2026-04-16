namespace ShopInventory.DTOs;

public class ValidateStockRequest
{
    public List<CreateStockReservationLineRequest> Lines { get; set; } = new();
    public string? ExcludeReservationId { get; set; }
}
