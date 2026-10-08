namespace ShopInventory.DTOs;

/// <summary>A page of the delivery log, newest first.</summary>
public sealed class CustomerDocumentDeliveryPageDto
{
    public List<CustomerDocumentDeliveryDto> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}
