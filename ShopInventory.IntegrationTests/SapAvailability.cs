using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using ShopInventory.Configuration;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Decides once per run whether a real SAP Service Layer can be reached, and why not when it
/// cannot.
/// </summary>
/// <remarks>
/// These tests are meant to be run by a developer on the SAP network and skipped everywhere else.
/// Skipped, not silently passed: a test that returns early looks identical to one that ran, which
/// is the failure mode this whole project exists to close.
/// </remarks>
public static class SapAvailability
{
    // Measured TCP connect times to the SAP host swing between 0.2s and 3.7s depending on how busy
    // it is. At 2000ms that turned into an intermittent skip of the whole suite — which reads
    // exactly like a clean run, the failure mode this class exists to prevent. Kept short enough
    // that a machine genuinely off the network still fails discovery quickly.
    private const int ProbeTimeoutMilliseconds = 15000;

    private static readonly Lazy<(SAPSettings? Settings, string? SkipReason)> Probe = new(Evaluate);

    /// <summary>Null when SAP is usable; otherwise the reason to report as the skip message.</summary>
    public static string? SkipReason => Probe.Value.SkipReason;

    public static SAPSettings Settings =>
        Probe.Value.Settings
        ?? throw new InvalidOperationException(Probe.Value.SkipReason ?? "SAP is not available.");

    private static (SAPSettings?, string?) Evaluate()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(SapAvailability).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var settings = new SAPSettings();
        configuration.GetSection("SAP").Bind(settings);

        if (string.IsNullOrWhiteSpace(settings.ServiceLayerUrl)
            || string.IsNullOrWhiteSpace(settings.CompanyDB)
            || string.IsNullOrWhiteSpace(settings.Username)
            || string.IsNullOrWhiteSpace(settings.Password))
        {
            return (null,
                "SAP is not configured for this machine. Set SAP:ServiceLayerUrl, SAP:CompanyDB, "
                + "SAP:Username and SAP:Password in user secrets or the environment.");
        }

        if (!Uri.TryCreate(settings.ServiceLayerUrl, UriKind.Absolute, out var serviceLayerUri))
        {
            return (null, $"SAP:ServiceLayerUrl is not a valid absolute URL: '{settings.ServiceLayerUrl}'.");
        }

        // A TCP probe rather than a Login: this runs at discovery, and the answer only needs to be
        // "is there anything listening", not "are the credentials good".
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(serviceLayerUri.Host, serviceLayerUri.Port)
                    .Wait(ProbeTimeoutMilliseconds))
            {
                return (null,
                    $"SAP Service Layer at {serviceLayerUri.Host}:{serviceLayerUri.Port} did not answer within "
                    + $"{ProbeTimeoutMilliseconds}ms. Run these on the SAP network.");
            }
        }
        catch (Exception ex)
        {
            return (null,
                $"SAP Service Layer at {serviceLayerUri.Host}:{serviceLayerUri.Port} is unreachable: {ex.Message}");
        }

        return (settings, null);
    }
}

/// <summary>
/// A fact that skips, with a reason, when SAP cannot be reached.
/// </summary>
public sealed class SapFactAttribute : FactAttribute
{
    public SapFactAttribute()
    {
        Skip = SapAvailability.SkipReason;
    }
}

/// <summary>
/// A theory that skips, with a reason, when SAP cannot be reached.
/// </summary>
public sealed class SapTheoryAttribute : TheoryAttribute
{
    public SapTheoryAttribute()
    {
        Skip = SapAvailability.SkipReason;
    }
}
