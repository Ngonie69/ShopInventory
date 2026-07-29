namespace ShopInventory.DTOs;

public class WhatsAppInboxResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public List<WhatsAppInboxItemDto> Messages { get; set; } = new();
}