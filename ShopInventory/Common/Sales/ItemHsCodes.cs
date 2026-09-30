using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The HS code each item is declared under, from the local copy of the item master.
/// </summary>
/// <remarks>
/// SAP holds an item's HS code in <c>OITM.FrgnName</c> (<c>ForeignName</c> on the Service Layer), and
/// the Fiscalisation platform already reads it there for every document it fiscalises out of SAP. A
/// receipt this app builds itself — a till sale, a van sale fiscalised ahead of its invoice — used to
/// stamp <see cref="Configuration.FiscalisationSettings.DefaultHsCode"/> on every line instead, so a
/// bun and a juice were both declared to ZIMRA as 04031000, yoghurt.
///
/// <para>
/// Read from <c>SapItemTaxGroups</c> and never from SAP, for the reason <see cref="ItemVatGroups"/>
/// gives: the customer is at the counter. The Item Tax Groups sync fills it in the same pass as the VAT
/// group.
/// </para>
/// </remarks>
public interface IItemHsCodes
{
    /// <summary>
    /// The stored HS code for each of <paramref name="itemCodes"/> that has one. Empty on any failure: a
    /// line with no answer falls back to the configured default, and a sale is never refused over it.
    /// </summary>
    Task<Dictionary<string, string>> ResolveAsync(IEnumerable<string?> itemCodes, CancellationToken ct);
}

public sealed class ItemHsCodes(ApplicationDbContext context, ILogger<ItemHsCodes> logger) : IItemHsCodes
{
    public async Task<Dictionary<string, string>> ResolveAsync(IEnumerable<string?> itemCodes, CancellationToken ct)
    {
        var codes = itemCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var rows = await context.SapItemTaxGroups
                .AsNoTracking()
                .Where(row => codes.Contains(row.ItemCode) && row.HsCode != null)
                .Select(row => new { row.ItemCode, row.HsCode })
                .ToListAsync(ct);

            var resolved = rows
                .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().HsCode!, StringComparer.OrdinalIgnoreCase);

            var missing = codes.Where(code => !resolved.ContainsKey(code)).ToList();
            if (missing.Count > 0)
            {
                logger.LogWarning(
                    "No HS code stored for {Count} item(s) on this receipt: {Items}. They are declared under "
                    + "the default HS code. Set OITM.FrgnName in SAP and run the Item Tax Groups sync.",
                    missing.Count, string.Join(", ", missing));
            }

            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not read item HS codes; this receipt is declared under the default HS code.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The item master's foreign name as an HS code FDMS will take, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Pads a short all-digit code the way the platform's <c>SapReceiptMapper.CleanAndPadHsCode</c> does,
    /// so both paths declare the same item the same way: a code typed or pasted through Excel loses its
    /// leading zero (4031000 for 04031000). Anything that is still not 4 or 8 digits is not stored — FDMS
    /// refuses it with RCPT048, and a till sale is better declared under the default than not at all.
    /// </remarks>
    public static string? Normalize(string? foreignName)
    {
        if (string.IsNullOrWhiteSpace(foreignName))
        {
            return null;
        }

        var code = foreignName.Trim();
        if (!code.All(char.IsAsciiDigit))
        {
            return null;
        }

        code = code.Length switch
        {
            > 4 and < 8 => code.PadLeft(8, '0'),
            < 4 => code.PadLeft(4, '0'),
            _ => code
        };

        return code.Length is 4 or 8 ? code : null;
    }
}
