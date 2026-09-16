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
/// The column is 254 characters. A typical sale needs about 200, so normally everything fits. When it
/// does not, parts go in the reverse of how much they matter: the tender first, then the time of sale,
/// then who captured it, then where it came from, then who bought, then the reference. The fiscal
/// receipt goes last of all, because it is the join between this invoice and the ZIMRA receipt the
/// customer holds, and nothing else on the document carries it.
/// </para>
/// </remarks>
public static class DesktopSaleInvoiceRemarks
{
    /// <summary>OINV.Comments is a single 254-character column in SAP Business One.</summary>
    public const int MaxLength = 254;

    private const string Separator = " | ";

    /// <summary>
    /// How much each part matters, for the order they are given up in when the column is short.
    /// </summary>
    /// <remarks>
    /// Named rather than written as literals at each call, because the ranking is only meaningful as a
    /// whole: a value repeated or skipped makes the drop order arbitrary between two parts, and which
    /// one an auditor loses then depends on list order rather than on anything anybody decided.
    /// </remarks>
    private static class Keep
    {
        public const int Payment = 0;
        public const int SoldAt = 1;
        public const int CapturedBy = 2;
        public const int Origin = 3;
        public const int Party = 4;
        public const int Reference = 5;
        public const int FiscalReceipt = 6;
    }

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

        Add(DescribeOrigin(sale.SourceSystem, sale.WarehouseCode, names.ShopName), Keep.Origin);
        Add(DescribeParty(sale.SourceSystem, sale.RouteCustomerCode, sale.RouteCustomerName), Keep.Party);
        Add(DescribeReference(sale.ExternalReferenceId), Keep.Reference);
        Add(DescribeFiscalReceipt(sale), Keep.FiscalReceipt);
        Add(DescribeCapturedBy(names.CapturedBy), Keep.CapturedBy);
        Add(DescribeSoldAt(sale.ReceiptDate ?? sale.CreatedAt), Keep.SoldAt);
        Add(DescribePayment(sale.PaymentMethod, sale.PaymentReference), Keep.Payment);

        return Assemble(parts);
    }

    /// <summary>
    /// The same remark for an online van sale, whose invoice posts from a reservation before any sale
    /// row exists to build it from.
    /// </summary>
    /// <remarks>
    /// Those invoices used to reach SAP saying "Posted from reservation 41c8…", which names a row in
    /// this system's own table and nothing a person can act on. They carry no receipt part: the sale is
    /// fiscalised from the invoice itself, minutes later and by a different path, so there is no receipt
    /// to quote at the moment this is written.
    /// </remarks>
    public static string BuildForOnlineVanSale(
        string? warehouseCode,
        string? shopName,
        string? routeCustomerCode,
        string? routeCustomerName,
        string? externalReference,
        string? capturedBy,
        string? paymentMethod,
        DateTime soldAtUtc)
    {
        var parts = new List<(string Text, int Keep)>();

        void Add(string? text, int keep)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add((text, keep));
            }
        }

        Add(DescribeOrigin(SaleSourceSystems.VanSalesOnline, warehouseCode, shopName), Keep.Origin);
        Add(DescribeParty(SaleSourceSystems.VanSalesOnline, routeCustomerCode, routeCustomerName), Keep.Party);
        Add(DescribeReference(externalReference), Keep.Reference);
        Add(DescribeCapturedBy(capturedBy), Keep.CapturedBy);
        Add(DescribeSoldAt(soldAtUtc), Keep.SoldAt);
        Add(DescribePayment(paymentMethod, paymentReference: null), Keep.Payment);

        return Assemble(parts);
    }

    /// <summary>
    /// Joins the parts, giving up the least important until the column will take them.
    /// </summary>
    private static string Assemble(List<(string Text, int Keep)> parts)
    {
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
                    capturedBy = user is null
                        ? null
                        : SaleOperatorNames.Format(user.FirstName, user.LastName, user.Username);
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

    private static string? DescribeReference(string? externalReference) =>
        string.IsNullOrWhiteSpace(externalReference) ? null : $"Ref {externalReference.Trim()}";

    private static string? DescribeCapturedBy(string? capturedBy) =>
        string.IsNullOrWhiteSpace(capturedBy) ? null : $"Captured by {capturedBy.Trim()}";

    /// <summary>
    /// When the sale actually happened, to the minute.
    /// </summary>
    /// <remarks>
    /// <c>DocDate</c> is the trading day and nothing else on the document narrows it, so until this the
    /// time of a sale existed nowhere in SAP — which is the one thing an investigation into a disputed
    /// takings figure asks for first. Read from <see cref="DesktopSaleEntity.ReceiptDate"/> where there
    /// is one, because that is the taxpayer's wall clock as the receipt was signed in it, and it is
    /// what the customer's copy shows; <see cref="DesktopSaleEntity.CreatedAt"/> is the fallback and is
    /// UTC, which for a Zimbabwean trading day differs by two hours. Both are labelled by the same
    /// caller-visible text deliberately: a remark that said "UTC" on some sales and not others would
    /// invite the reader to assume the unlabelled ones are local.
    /// </remarks>
    private static string? DescribeSoldAt(DateTime soldAt) =>
        soldAt == default ? null : $"Sold {soldAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Who bought, for the routes where <c>CardCode</c> does not say.
    /// </summary>
    /// <remarks>
    /// A van invoices its own business partner and a vending sale is billed to the depot, so on both the
    /// billed account is the same on every sale and answers "which van" or "which depot" rather than
    /// "who bought". This is also on the invoice's <c>NumAtCard</c> — see
    /// <see cref="DesktopSaleCustomerReference"/> — and is repeated here rather than left to it, because
    /// that field takes one value and is capped: a van names the shop and loses the code, vending names
    /// the code and loses the shop, and a long name is truncated outright. Here both fit.
    ///
    /// <para>
    /// Null for a shop till, which sells over a counter and has no counterparty to name.
    /// </para>
    /// </remarks>
    private static string? DescribeParty(string? sourceSystem, string? code, string? name)
    {
        var label = SaleSourceSystems.IsVanSale(sourceSystem)
            ? "Customer"
            : string.Equals(sourceSystem?.Trim(), SaleSourceSystems.Vending, StringComparison.OrdinalIgnoreCase)
                ? "Vendor"
                : null;

        if (label is null)
        {
            return null;
        }

        var trimmedCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        var trimmedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        return (trimmedCode, trimmedName) switch
        {
            (null, null) => null,
            (null, _) => $"{label} {trimmedName}",
            (_, null) => $"{label} {trimmedCode}",
            _ => $"{label} {trimmedCode} — {trimmedName}"
        };
    }

    private static string? DescribeOrigin(string? sourceSystem, string? warehouseCode, string? shopName)
    {
        var source = sourceSystem?.Trim() switch
        {
            SaleSourceSystems.ShopTill => "Shop till",
            SaleSourceSystems.Vending => "Vending",
            SaleSourceSystems.VanSales => "Van sale",
            SaleSourceSystems.VanSalesOnline => "Van sale",
            null or "" => null,
            var other => other
        };

        var code = string.IsNullOrWhiteSpace(warehouseCode) ? null : warehouseCode.Trim();
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
            // The formatter strips a code stored already hyphenated before regrouping it, so the
            // separators a device supplied are never counted as characters.
            details.Add($"code {FiscalReceiptQrComposer.FormatVerificationCode(sale.FiscalVerificationCode)}");
        }

        return details.Count == 0 ? null : "Fiscal " + string.Join(", ", details);
    }

    private static string? DescribePayment(string? paymentMethod, string? paymentReference)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(paymentReference)
            ? $"Paid {paymentMethod.Trim()}"
            : $"Paid {paymentMethod.Trim()} ({paymentReference.Trim()})";
    }
}
