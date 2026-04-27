namespace ShopInventory.DTOs;

public sealed class MobileBiometricPreferenceRequest
{
    public bool Enabled { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? BiometricCapability { get; set; }
}