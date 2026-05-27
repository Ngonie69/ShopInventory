namespace ShopInventory.DTOs;

public class BulkCratePodValidationRequestDto
{
    public List<int> InvoiceDocNums { get; set; } = [];
    public string? SubmissionRole { get; set; }
}