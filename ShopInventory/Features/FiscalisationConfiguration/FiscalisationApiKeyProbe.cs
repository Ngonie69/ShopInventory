using System.Net;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalisationConfiguration;

public enum FiscalisationApiKeyVerdict
{
    /// <summary>The platform authenticated the key and served the request.</summary>
    Accepted,

    /// <summary>The platform refused the key itself — unknown, revoked, or missing a scope.</summary>
    Rejected,

    /// <summary>The platform could not be asked, so the key is neither proven nor disproven.</summary>
    Inconclusive
}

public sealed record FiscalisationApiKeyProbeResult(FiscalisationApiKeyVerdict Verdict, string Message)
{
    public bool IsAccepted => Verdict == FiscalisationApiKeyVerdict.Accepted;

    public bool IsRejected => Verdict == FiscalisationApiKeyVerdict.Rejected;
}

/// <summary>
/// Asks the Fiscalisation platform whether it accepts an API key, by reading a device's configuration
/// with it.
/// </summary>
/// <remarks>
/// <c>GET /api/fiscal-config</c> is the cheapest authenticated call the platform has: it reads, it
/// touches no fiscal day, and it cannot produce a receipt. Nothing here may ever probe with a call that
/// submits, because a fiscal receipt cannot be withdrawn.
///
/// The three-way verdict is the point. Only the platform saying "not this key" disqualifies a key; a
/// platform that is unreachable, or that has no such device, has told us nothing about the key, and
/// treating that as a rejection would block an administrator from installing a good key during exactly
/// the outage they are trying to fix.
/// </remarks>
public static class FiscalisationApiKeyProbe
{
    public static async Task<FiscalisationApiKeyProbeResult> RunAsync(
        IFiscalisationApiClient client,
        string? apiKey,
        int deviceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = await client.GetFiscalConfigWithApiKeyAsync(apiKey, deviceId, cancellationToken);

            var taxpayer = string.IsNullOrWhiteSpace(config.TaxPayerName)
                ? "the configured taxpayer"
                : config.TaxPayerName;

            // Naming a device here would be a lie when we did not ask for one: the platform picked it,
            // and it is free to pick a different one next time.
            var named = deviceId > 0 ? $"device {deviceId}" : "the console's own device";
            var device = string.IsNullOrWhiteSpace(config.DeviceSerialNo)
                ? named
                : $"{named} ({config.DeviceSerialNo})";

            return new FiscalisationApiKeyProbeResult(
                FiscalisationApiKeyVerdict.Accepted,
                $"Key accepted. Read {device} for {taxpayer}, in {config.DeviceOperatingMode} mode.");
        }
        catch (FiscalisationApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("Fiscalisation API key was rejected by the platform: {Detail}", ex.Message);

            return new FiscalisationApiKeyProbeResult(
                FiscalisationApiKeyVerdict.Rejected,
                "The Fiscalisation platform did not accept this API key. Check it was copied whole from "
                + "the console's API Keys page and has not been revoked.");
        }
        catch (FiscalisationApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            logger.LogWarning("Fiscalisation API key lacks the required access: {Detail}", ex.Message);

            return new FiscalisationApiKeyProbeResult(
                FiscalisationApiKeyVerdict.Rejected,
                "The platform recognised the key but refused the request. It needs the receipt.submit, "
                + "sap.fiscalise and device.read scopes, and no device allowlist — a device-scoped key "
                + $"breaks failover. The platform said: {ex.Message}");
        }
        catch (FiscalisationApiException ex)
        {
            // Past the API-key check, so the key is not what was refused — unless nothing answered at
            // all, which is what a route this platform build does not serve looks like from here.
            logger.LogWarning(
                ex,
                "Fiscalisation key probe failed on device {DeviceId} with HTTP {StatusCode}/{ErrorCode}",
                deviceId,
                (int)ex.StatusCode,
                ex.ErrorCode);

            var reason = ex.HasProblemDocument
                ? $"reading {(deviceId > 0 ? $"device {deviceId}" : "the console's own device")} failed: "
                  + ex.Message
                : $"the platform at this address answered HTTP {(int)ex.StatusCode} with no explanation, "
                  + "which usually means it is not a Fiscalisation console.";

            return new FiscalisationApiKeyProbeResult(
                FiscalisationApiKeyVerdict.Inconclusive,
                $"The key was not refused, but {reason}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach the Fiscalisation platform to check an API key");

            return new FiscalisationApiKeyProbeResult(
                FiscalisationApiKeyVerdict.Inconclusive,
                $"Could not reach the Fiscalisation platform: {ex.Message}");
        }
    }
}
