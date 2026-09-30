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
    /// <summary>
    /// How many of the platform's devices an unpinned probe reads before giving up. The list includes
    /// devices the console has merely seen, and each read is a round trip to FDMS, so a console full
    /// of retired handsets must not turn a save into a minute-long wait.
    /// </summary>
    internal const int MaxDevicesTried = 3;

    public static async Task<FiscalisationApiKeyProbeResult> RunAsync(
        IFiscalisationApiClient client,
        string? apiKey,
        int deviceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The device the last read asked for, so a failure can say which one it was about. Zero is the
        // platform's own device, which only an unpinned read that found no list to choose from asks for.
        var attemptedDeviceId = deviceId;

        async Task<FiscalConfigApiResponse> ReadAsync()
        {
            if (deviceId > 0)
            {
                return await client.GetFiscalConfigWithApiKeyAsync(apiKey, deviceId, cancellationToken);
            }

            // Leaving the device off lets the platform answer for its own Fdms:DeviceId, and a console
            // that fiscalises across several devices leaves that unset — then the read is refused as
            // "DeviceId is required" and a good key reads as unverifiable. So ask which devices it has
            // and read one of those. A key refused the list is refused, full stop: the list is served to
            // every key with no device allowlist, and that is the only kind this API can use.
            IReadOnlyList<int> known;
            try
            {
                known = await client.GetKnownDeviceIdsWithApiKeyAsync(apiKey, cancellationToken);
            }
            catch (FiscalisationApiException ex) when (
                ex.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                logger.LogInformation(
                    "Fiscalisation platform did not list its devices (HTTP {StatusCode}/{ErrorCode}); "
                    + "letting it choose the device",
                    (int)ex.StatusCode,
                    ex.ErrorCode);
                known = [];
            }

            FiscalisationApiException? lastFailure = null;
            foreach (var candidate in known.Where(id => id > 0).Distinct().Take(MaxDevicesTried))
            {
                attemptedDeviceId = candidate;
                try
                {
                    return await client.GetFiscalConfigWithApiKeyAsync(apiKey, candidate, cancellationToken);
                }
                catch (FiscalisationApiException ex) when (
                    ex.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                {
                    // A retired or unreachable device says nothing about the key; try the next one.
                    lastFailure = ex;
                }
            }

            if (lastFailure is not null)
            {
                throw lastFailure;
            }

            attemptedDeviceId = 0;
            return await client.GetFiscalConfigWithApiKeyAsync(apiKey, 0, cancellationToken);
        }

        try
        {
            var config = await ReadAsync();

            var taxpayer = string.IsNullOrWhiteSpace(config.TaxPayerName)
                ? "the configured taxpayer"
                : config.TaxPayerName;

            // Naming a device here would be a lie when we did not ask for one: the platform picked it,
            // and it is free to pick a different one next time.
            var named = attemptedDeviceId > 0 ? $"device {attemptedDeviceId}" : "the console's own device";
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
                attemptedDeviceId,
                (int)ex.StatusCode,
                ex.ErrorCode);

            var reason = ex.HasProblemDocument
                ? $"reading {(attemptedDeviceId > 0 ? $"device {attemptedDeviceId}" : "the console's own device")} failed: "
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
