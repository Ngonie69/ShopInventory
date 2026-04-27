using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.CompleteMobileBiometricLogin;

public sealed class CompleteMobileBiometricLoginHandler(
    IAuthService authService,
    IAuditService auditService,
    ILogger<CompleteMobileBiometricLoginHandler> logger
) : IRequestHandler<CompleteMobileBiometricLoginCommand, ErrorOr<AuthLoginResponse>>
{
    public async Task<ErrorOr<AuthLoginResponse>> Handle(
        CompleteMobileBiometricLoginCommand command,
        CancellationToken cancellationToken)
    {
        var result = await authService.RefreshTokenAsync(command.RefreshToken, command.IpAddress);
        var deviceDetails = BuildDeviceDetails(command.DeviceId, command.DeviceName, command.BiometricCapability);

        if (result is null)
        {
            logger.LogWarning("Invalid mobile biometric login attempt from IP: {IpAddress}", command.IpAddress);
            try
            {
                await auditService.LogAsync(
                    AuditActions.MobileBiometricLoginFailed,
                    "Unknown",
                    "Unknown",
                    "MobileDevice",
                    Clean(command.DeviceId),
                    $"Failed mobile biometric login. {deviceDetails}",
                    null,
                    false,
                    "Invalid or expired refresh token");
            }
            catch { }

            return Errors.Auth.InvalidRefreshToken;
        }

        var username = result.User?.Username ?? "Unknown";
        var role = result.User?.Role ?? "Unknown";
        try
        {
            await auditService.LogAsync(
                AuditActions.MobileBiometricLogin,
                username,
                role,
                "MobileDevice",
                Clean(command.DeviceId),
                $"Mobile biometric login for {username}. {deviceDetails}");
        }
        catch { }

        return result;
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