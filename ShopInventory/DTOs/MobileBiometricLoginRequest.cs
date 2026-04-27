namespace ShopInventory.DTOs;

public sealed class MobileBiometricLoginRequest
{
    public required string RefreshToken { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? BiometricCapability { get; set; }
}