namespace ShopInventory.DTOs;

/// <summary>
/// Standard error response DTO
/// </summary>
public class ErrorResponseDto
{
    public string Message { get; set; } = string.Empty;
    public List<string>? Errors { get; set; }
}

/// <summary>
/// Standard success response DTO
/// </summary>
public class SuccessResponseDto
{
    public string Message { get; set; } = string.Empty;
}
