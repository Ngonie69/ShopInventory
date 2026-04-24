namespace ShopInventory.DTOs;

public sealed class PasskeyRegistrationOptionsResponse
{
    public string SessionToken { get; set; } = string.Empty;

    public string OptionsJson { get; set; } = string.Empty;
}