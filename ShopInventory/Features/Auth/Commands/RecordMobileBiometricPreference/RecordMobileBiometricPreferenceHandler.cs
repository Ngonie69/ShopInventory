using ErrorOr;
using MediatR;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.RecordMobileBiometricPreference;

public sealed class RecordMobileBiometricPreferenceHandler(
    IAuditService auditService
) : IRequestHandler<RecordMobileBiometricPreferenceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RecordMobileBiometricPreferenceCommand command,
        CancellationToken cancellationToken)
    {
        var action = command.Enabled
            ? AuditActions.EnableMobileBiometricLogin
            : AuditActions.DisableMobileBiometricLogin;

        var state = command.Enabled ? "enabled" : "disabled";
        var details = $"Mobile biometric login {state}. {BuildDeviceDetails(command.DeviceId, command.DeviceName, command.BiometricCapability)}";

        try
        {
            await auditService.LogAsync(action, "MobileDevice", Clean(command.DeviceId), details, true);
        }
        catch { }

        return Result.Success;
    }

    private static string BuildDeviceDetails(string? deviceId, string? deviceName, string? biometricCapability)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(deviceId))
            parts.Add($"deviceId={Clean(deviceId)}");

        if (!string.IsNullOrWhiteSpace(deviceName))
            parts.Add($"device={Clean(deviceName)}");

        if (!string.IsNullOrWhiteSpace(biometricCapability))
            parts.Add($"capability={Clean(biometricCapability)}");

        return parts.Count == 0 ? "Device details unavailable." : string.Join("; ", parts);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = value.Trim();
        return cleaned.Length <= 128 ? cleaned : cleaned[..128];
    }
}