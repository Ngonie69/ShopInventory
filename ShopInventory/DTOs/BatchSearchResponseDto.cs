namespace ShopInventory.DTOs;

public sealed class BatchSearchResponseDto
{
    public string SearchTerm { get; set; } = string.Empty;
    public int ResultCount { get; set; }
    public List<BatchSearchResultDto> Results { get; set; } = new();
}