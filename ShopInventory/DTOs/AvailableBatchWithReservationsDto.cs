namespace ShopInventory.DTOs;

public class AvailableBatchWithReservationsDto
{
    public string BatchNumber { get; set; } = string.Empty;
    public decimal PhysicalQuantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? ManufacturingDate { get; set; }
    public string? Status { get; set; }
}
