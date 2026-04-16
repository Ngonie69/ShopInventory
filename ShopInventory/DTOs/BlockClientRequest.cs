namespace ShopInventory.DTOs;

public class BlockClientRequest
{
    public int DurationMinutes { get; set; } = 60;
    public string? Reason { get; set; }
}
