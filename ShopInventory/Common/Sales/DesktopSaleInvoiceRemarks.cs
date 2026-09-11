using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The names a remark shows that the sale itself only holds as codes.
/// </summary>
public sealed record DesktopSaleRemarkNames(string? ShopName, string? CapturedBy)
{
    public static readonly DesktopSaleRemarkNames None = new(null, null);
}

/// <summary>
/// Builds the Remarks a system-posted desktop sale invoice carries in SAP.
/// </summary>
/// <remarks>
/// Someone reading the invoice in SAP has no other way to see where it came from. Till invoices used
/// to arrive with the column blank, and the van route wrote a short line of its own, so the same
/// question - which shop, which receipt, who rang it up - had a different answer depending on which
/// route happened to post. One builder, used by both.
///
/// <para>
/// The column is 254 characters. A typical sale needs about 165, so normally everything fits. When it
/// does not, parts go in the reverse of how much they matter: payment first, then who captured it,
/// then where it came from, then the reference. The fiscal receipt goes last of all, because it is
/// the join between this invoice and the ZIMRA receipt the customer holds, and nothing else on the
/// document carries it.
/// </para>
/// </remarks>
public static class DesktopSaleInvoiceRemarks
{
    /// <summary>OINV.Comments is a single 254-character column in SAP Business One.</summary>
    public const int MaxLength = 254;

    private const string Separator = " | ";

    public static string Build(DesktopSaleEntity sale, DesktopSaleRemarkNames names)
    {
        // Laid out in reading order; Keep says what survives when the column is short.
        var parts = new List<(string Text, int Keep)>();

        void Add(string? text, int keep)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add((text, keep));
            }
        }

        Add(DescribeOrigin(sale, names.ShopName), keep: 2);
        Add(string.IsNullOrWhiteSpace(sale.ExternalReferenceId) ? null : $"Ref {sale.ExternalReferenceId.Trim()}", keep: 3);
        Add(DescribeFiscalReceipt(sale), keep: 4);
        Add(string.IsNullOrWhiteSpace(names.CapturedBy) ? null : $"Captured by {names.CapturedBy.Trim()}", keep: 1);
        Add(DescribePayment(sale), keep: 0);

        var text = string.Join(Separator, parts.Select(part => part.Text));

        while (text.Length > MaxLength && parts.Count > 1)
        {
            parts.Remove(parts.MinBy(part => part.Keep));
            text = string.Join(Separator, parts.Select(part => part.Text));
        }

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    /// <summary>
    /// Looks up the shop and the account that captured the sale, for <see cref="Build"/>.
    /// </summary>
    /// <remarks>
    /// Never fails a post. A remark is for the person reading the invoice; losing a name from it is
    /// worth a warning, and refusing to put a fiscalised sale in SAP over it is not.
    /// </remarks>
    public static async Task<DesktopSaleRemarkNames> ResolveNamesAsync(
        ApplicationDbContext context,
        DesktopSaleEntity sale,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            string? shopName = null;
            if (!string.IsNullOrWhiteSpace(sale.WarehouseCode))
            {
                // WarehouseCode is indexed but not unique, so an active shop is preferred and the
                // order is stated rather than left to the database.
                shopName = await context.Shops
                    .AsNoTracking()
                    .Where(shop => shop.WarehouseCode == sale.WarehouseCode)
                    .OrderByDescending(shop => shop.IsActive)
                    .ThenBy(shop => shop.Id)
                    .Select(shop => shop.Name)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            string? capturedBy = null;
            var createdBy = sale.CreatedBy?.Trim();
            if (!string.IsNullOrEmpty(createdBy))
            {
                if (Guid.TryParse(createdBy, out var userId))
                {
                    var user = await context.Users
                        .AsNoTracking()
                        .Where(candidate => candidate.Id == userId)
                        .OrderBy(candidate => candidate.Id)
                        .Select(candidate => new { candidate.FirstName, candidate.LastName, candidate.Username })
                        .FirstOrDefaultAsync(cancellationToken);

                    // An account that no longer exists shows nothing, never the bare id: a GUID in a
                    // remark tells the reader less than a blank does.
                    capturedBy = user is null ? null : DisplayName(user.FirstName, user.LastName, user.Username);
                }
                else
                {
                    // Rows from before accounts were ids carried the name itself.
                    capturedBy = createdBy;
                }
            }

            return new DesktopSaleRemarkNames(shopName, capturedBy);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(
                exception,
                "Could not look up the shop or cashier for sale {ExternalReference}; its SAP remarks will go without them.",
                sale.ExternalReferenceId);

            return DesktopSaleRemarkNames.None;
        }
    }

    private static string? DescribeOrigin(DesktopSaleEntity sale, string? shopName)
    {
        var source = sale.SourceSystem?.Trim() switch
        {
            SaleSourceSystems.ShopTill => "Shop till",
            SaleSourceSystems.Vending => "Vending",
            SaleSourceSystems.VanSales => "Van sale",
            null or "" => null,
            var other => other
        };

        var code = string.IsNullOrWhiteSpace(sale.WarehouseCode) ? null : sale.WarehouseCode.Trim();
        var name = string.IsNullOrWhiteSpace(shopName) ? null : shopName.Trim();

        var place = (name, code) switch
        {
            (null, null) => null,
            (null, _) => code,
            (_, null) => name,
            _ => $"{name} ({code})"
        };

        return (source, place) switch
        {
            (null, null) => null,
            (null, _) => place,
            (_, null) => source,
            _ => $"{source}, {place}"
        };
    }

    private static string? DescribeFiscalReceipt(DesktopSaleEntity sale)
    {
        // Said outright, so a blank is never mistaken for a receipt that went missing.
        if (sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Skipped)
        {
            return "Not fiscalised";
        }

        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(sale.FiscalDeviceNumber))
        {
            details.Add($"device {sale.FiscalDeviceNumber.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(sale.FiscalDayNo))
        {
            details.Add($"day {sale.FiscalDayNo.Trim()}");
        }

        // FiscalReceiptNumber first. The till route writes only that one; ReceiptGlobalNo is filled by
        // the van route alone, and reading it first is exactly how till invoices would lose their
        // receipt number while van invoices kept theirs.
        var receipt = string.IsNullOrWhiteSpace(sale.FiscalReceiptNumber)
            ? sale.ReceiptGlobalNo?.ToString(CultureInfo.InvariantCulture)
            : sale.FiscalReceiptNumber.Trim();

        if (!string.IsNullOrWhiteSpace(receipt))
        {
            details.Add($"receipt {receipt}");
        }

        if (!string.IsNullOrWhiteSpace(sale.FiscalVerificationCode))
        {
            // Stripped to its characters before grouping, so a code stored already hyphenated is not
            // chopped into nonsense by grouping the hyphens too.
            var raw = new string(sale.FiscalVerificationCode.Where(char.IsLetterOrDigit).ToArray());
            details.Add($"code {FiscalReceiptQrComposer.FormatVerificationCode(raw)}");
        }

        return details.Count == 0 ? null : "Fiscal " + string.Join(", ", details);
    }

    private static string? DescribePayment(DesktopSaleEntity sale)
    {
        if (string.IsNullOrWhiteSpace(sale.PaymentMethod))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(sale.PaymentReference)
            ? $"Paid {sale.PaymentMethod.Trim()}"
            : $"Paid {sale.PaymentMethod.Trim()} ({sale.PaymentReference.Trim()})";
    }

    private static string DisplayName(string? firstName, string? lastName, string username)
    {
        var full = string.Join(
            " ",
            new[] { firstName, lastName }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim()));

        return full.Length > 0 ? full : username;
    }
}
