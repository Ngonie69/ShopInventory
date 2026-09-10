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
    /// The tax table for a handset the office fiscalises for, built from configuration rather than from
    /// a fiscal device.
    /// </summary>
    /// <remarks>
    /// Under REVMax there is no device the handset can be told about: the card sits on the office
    /// network and signs there. But the handset still has to <em>price</em> a basket, and every money
    /// figure on every screen is quoted through the rates in this table — so it is issued even though
    /// nothing about signing is.
    ///
    /// <para>Both halves are keyed by the same SAP VAT group, which is what keeps them honest: the id
    /// comes from <c>Revmax:TaxIdMappings</c> and the rate that accompanies it from
    /// <c>Tax:RatesByTaxCode</c>, exactly the pair the online path declares the invoice with. A handset
    /// pricing a line and the receipt REVMax files for it therefore read the same two numbers.</para>
    ///
    /// <para>Zero-rated is the case that makes this worth doing at all. With no lease the handset falls
    /// back to a single standard percentage, so an <c>O0</c> item is shown to the rep, and charged to the
    /// customer, with 15.5% added to a line that carries no VAT.</para>
    ///
    /// <para>The fallback id is included deliberately: an item whose group is not listed resolves to
    /// <paramref name="defaultTaxId"/>, and leaving that id out of the table would make
    /// <see cref="BuildItemTaxes(IReadOnlyDictionary{string, string}, IReadOnlyDictionary{string, int},
    /// int, string?, IEnumerable{VanSalesFiscalTaxDto}, out IReadOnlyCollection{string})"/> drop every
    /// such item.</para>
    /// </remarks>
    /// <param name="taxIdsByVatGroup">SAP VAT group to the selected provider's own tax id.</param>
    /// <param name="defaultTaxId">The id an unlisted group resolves to, charged at <c>Tax:VatRate</c>.</param>
    /// <param name="taxSettings">Where the rate for each of those groups is read from.</param>
    /// <param name="conflictingTaxIds">
    /// Ids that two VAT groups gave two different rates. Left out rather than resolved, on this file's
    /// governing rule: the handset then refuses those items by name instead of pricing half the
    /// catalogue at whichever rate happened to be enumerated first.
    /// </param>
    public static List<VanSalesFiscalTaxDto> BuildProviderTaxes(
        IReadOnlyDictionary<string, int> taxIdsByVatGroup,
        int defaultTaxId,
        TaxSettings taxSettings,
        out IReadOnlyCollection<int> conflictingTaxIds)
    {
        ArgumentNullException.ThrowIfNull(taxIdsByVatGroup);
        ArgumentNullException.ThrowIfNull(taxSettings);

        // The rate is held as a fraction and the lease states a percentage, the same units FDMS reports
        // and the handset's own fallback is written in.
        var percentByTaxId = new Dictionary<int, decimal>();
        var codeByTaxId = new Dictionary<int, string>();
        var conflicts = new HashSet<int>();

        void Offer(int taxId, decimal percent, string code)
        {
            if (taxId <= 0)
            {
                return;
            }

            if (percentByTaxId.TryGetValue(taxId, out var seen))
            {
                if (seen != percent)
                {
                    conflicts.Add(taxId);
                }

                return;
            }

            percentByTaxId[taxId] = percent;
            codeByTaxId[taxId] = code;
        }

        foreach (var (vatGroup, taxId) in taxIdsByVatGroup)
        {
            if (string.IsNullOrWhiteSpace(vatGroup))
            {
                continue;
            }

            var code = vatGroup.Trim();
            Offer(taxId, taxSettings.RateFor(code) * 100m, code);
        }

        // The unlisted-group fallback, at the rate the same fallback is charged at.
        Offer(defaultTaxId, taxSettings.VatRate * 100m, string.Empty);

        conflictingTaxIds = conflicts;

        return percentByTaxId
            .Where(entry => !conflicts.Contains(entry.Key))
            .OrderBy(entry => entry.Key)
            .Select(entry => new VanSalesFiscalTaxDto
            {
                TaxId = entry.Key,
                Percent = entry.Value,
                Code = string.IsNullOrEmpty(codeByTaxId[entry.Key]) ? null : codeByTaxId[entry.Key]
            })
            .ToList();
    }

    /// <summary>
    /// Maps each item to an FDMS tax id through its SAP VAT group, using the same settings the online
    /// path already fiscalises with — so an offline receipt carries the tax and HS code the server would
    /// have given the identical sale.
    ///
    /// <paramref name="unmappedVatGroups"/> returns the groups that resolved to nothing. Reported rather
    /// than swallowed: it is the only signal that a slice of the catalogue cannot be sold offline, and
    /// the fix is a configuration change nobody will make if it is invisible.
    /// </summary>
    public static List<VanSalesFiscalItemTaxDto> BuildItemTaxes(
        IReadOnlyDictionary<string, string> vatGroupsByItem,
        FiscalisationSettings settings,
        IEnumerable<VanSalesFiscalTaxDto> taxes,
        out IReadOnlyCollection<string> unmappedVatGroups)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return BuildItemTaxes(
            vatGroupsByItem,
            settings.TaxIdMappings,
            settings.DefaultTaxId,
            settings.DefaultHsCode,
            taxes,
            out unmappedVatGroups);
    }

    /// <summary>
    /// The same mapping against whichever provider's tax ids apply, since REVMax and the in-house
    /// platform key the identical SAP VAT groups to ids of their own that must never be interchanged.
    /// </summary>
    public static List<VanSalesFiscalItemTaxDto> BuildItemTaxes(
        IReadOnlyDictionary<string, string> vatGroupsByItem,
        IReadOnlyDictionary<string, int> taxIdsByVatGroup,
        int defaultTaxId,
        string? defaultHsCode,
        IEnumerable<VanSalesFiscalTaxDto> taxes,
        out IReadOnlyCollection<string> unmappedVatGroups)
    {
        ArgumentNullException.ThrowIfNull(vatGroupsByItem);
        ArgumentNullException.ThrowIfNull(taxIdsByVatGroup);
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

            if (!taxIdsByVatGroup.TryGetValue(vatGroup.Trim(), out var taxId))
            {
                // The configured fallback, exactly as the online path uses it. An unset DefaultTaxId
                // leaves the item out rather than sending 0, which nothing would accept anyway.
                taxId = defaultTaxId;
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
                HsCode = defaultHsCode
            });
        }

        unmappedVatGroups = unmapped;
        return itemTaxes;
    }
}
