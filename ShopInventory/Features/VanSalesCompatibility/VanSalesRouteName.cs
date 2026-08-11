using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility;

/// <summary>
/// Resolves the name of the business partner a van sales user is assigned to.
/// </summary>
/// <remarks>
/// The assigned business partner <em>is</em> the rep's route, so its name is the only human-readable
/// half of the pair the handset shows. Without it the dashboard falls back to the code and a rep reads
/// "VAN001 · VAN010", which tells them nothing about where they are.
/// <para>
/// The handset used to work this out for itself by searching the login response's customer list for an
/// entry whose code matched its own business partner. That could only ever succeed by coincidence: the
/// list is the accounts on the route — who the van sells <em>to</em> — and a van is not one of its own
/// customers. The name is resolved here instead, where the business partner master actually is.
/// </para>
/// </remarks>
public static class VanSalesRouteName
{
    /// <summary>
    /// The assigned partner's name, or empty when there is none to be had.
    /// </summary>
    /// <remarks>
    /// Never throws, and never blocks a sign-in. A rep standing at the first stop of the day needs to
    /// get into the app whatever SAP is doing; a route shown by its code is a poor label, but a login
    /// refused because the business partner master could not be read is a rep who cannot work at all.
    /// The handset already renders the code when the name is absent, so empty degrades cleanly.
    /// </remarks>
    public static async Task<string> ResolveAsync(
        string? assignedBusinessPartnerCode,
        Func<string, CancellationToken, Task<BusinessPartnerDto?>> readPartner,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readPartner);
        ArgumentNullException.ThrowIfNull(logger);

        var code = assignedBusinessPartnerCode?.Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            // Not every user is assigned to a route — a depot controller signing in is not a van.
            return string.Empty;
        }

        try
        {
            var partner = await readPartner(code, cancellationToken);
            var name = partner?.CardName?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                logger.LogInformation(
                    "Business partner {BusinessPartnerCode} has no name to show for the route", code);
                return string.Empty;
            }

            return name;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not read the name of business partner {BusinessPartnerCode}; the route will show its code",
                code);

            return string.Empty;
        }
    }
}
