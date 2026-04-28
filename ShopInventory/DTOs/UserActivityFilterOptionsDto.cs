namespace ShopInventory.DTOs;

public sealed class UserActivityFilterOptionsDto
{
    public List<string> Users { get; init; } = [];
    public List<string> Actions { get; init; } = [];
}