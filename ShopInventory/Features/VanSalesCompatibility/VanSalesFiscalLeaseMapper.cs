using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.VanSalesCompatibility;

/// <summary>
/// Turns the device's fiscal configuration and SAP's VAT groups into the tax half of a van handset's
/// lease.
///
/// Separated from the handler because this is where an offline receipt's tax is decided, and it is worth
/// being able to prove without a network or a database. The governing rule throughout: where a fact is
/// missing, leave the item out. The handset refuses an unmapped item by name, whereas a plausible
/// default prints a receipt with the wrong tax on it — which nothing downstream can detect, and which
/// nobody discovers until ZIMRA rejects the fiscal day's file.
/// </summary>
public static class VanSalesFiscalLeaseMapper
{
    /// <summary>
    /// The device's applicable taxes, narrowed to those in force at <paramref name="nowLocal"/>.
    ///
    /// An expired rate left in the list is a rate the handset could still sign with days after it
    /// stopped applying, and it has no way to know better once it is offline. Where a tax id appears
    /// more than once — which is how a rate change is expressed — the most recently effective wins.
    /// </summary>
    public static List<VanSalesFiscalTaxDto> BuildTaxes(FiscalConfigApiResponse config, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ApplicableTaxes
            .Where(tax => tax.TaxValidFrom <= nowLocal)
            .Where(tax => tax.TaxValidTill is null || tax.TaxValidTill >= nowLocal)
            .GroupBy(tax => tax.TaxID)
            .Select(group => group.OrderByDescending(tax => tax.TaxValidFrom).First())
            .OrderBy(tax => tax.TaxID)
            .Select(tax => new VanSalesFiscalTaxDto
            {
                TaxId = tax.TaxID,
                Percent = tax.TaxPercent,
                Code = tax.TaxName
            })
            .ToList();
    }

    /// <summary>
    /// Maps each item to an FDMS tax id through its SAP VAT group, using the same settings the online
    /// path already fiscalises with — so an offline receipt carries the tax and HS code the server would
    /// have given the identical sale.
    /// </summary>
    /// <param name="unmappedVatGroups">
    /// The groups that resolved to nothing. Reported rather than swallowed: it is the only signal that a
    /// slice of the catalogue cannot be sold offline, and the fix is a configuration change nobody will
    /// make if it is invisible.
    /// </param>
    public static List<VanSalesFiscalItemTaxDto> BuildItemTaxes(
        IReadOnlyDictionary<string, string> vatGroupsByItem,
        FiscalisationSettings settings,
        IEnumerable<VanSalesFiscalTaxDto> taxes,
        out IReadOnlyCollection<string> unmappedVatGroups)
    {
        ArgumentNullException.ThrowIfNull(vatGroupsByItem);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(taxes);

        var knownTaxIds = taxes.Select(tax => tax.TaxId).ToHashSet();
        var unmapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemTaxes = new List<VanSalesFiscalItemTaxDto>();

        foreach (var (itemCode, vatGroup) in vatGroupsByItem)
        {
            if (string.IsNullOrWhiteSpace(itemCode) || string.IsNullOrWhiteSpace(vatGroup))
            {
                continue;
            }

            if (!settings.TaxIdMappings.TryGetValue(vatGroup.Trim(), out var taxId))
            {
                // The configured fallback, exactly as the online path uses it. An unset DefaultTaxId
                // leaves the item out rather than sending 0, which nothing would accept anyway.
                taxId = settings.DefaultTaxId;
            }

            // A tax id the device's own configuration does not list cannot be signed against, whether it
            // came from the mapping or the fallback.
            if (taxId <= 0 || !knownTaxIds.Contains(taxId))
            {
                unmapped.Add(vatGroup.Trim());
                continue;
            }

            itemTaxes.Add(new VanSalesFiscalItemTaxDto
            {
                ItemCode = itemCode,
                TaxId = taxId,
                HsCode = settings.DefaultHsCode
            });
        }

        unmappedVatGroups = unmapped;
        return itemTaxes;
    }
}
