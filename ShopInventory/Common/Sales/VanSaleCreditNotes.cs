using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Which credit notes belong to van sales, for the van credit notes list.
/// </summary>
/// <remarks>
/// Nothing on a credit note says it came off a van — not in SAP, not in the local tables. What does is
/// the invoice it reverses: a credit note is a van credit note when any of its lines is based on an A/R
/// invoice (<see cref="InvoiceBaseType"/>) that the van app raised. A credit note with no invoice-based
/// line cannot be traced to a van and stays on the ordinary list.
///
/// <para>
/// The van invoices are read from this database only, never from SAP: the confirmed van reservations
/// (online van sales and converted orders post from those) and the van <c>DesktopSales</c> rows that
/// SAP has given a DocEntry. Every list path — the projection, the SAP fallback and the local table —
/// asks this class, so the three cannot disagree about which credit notes are a van's.
/// </para>
/// </remarks>
public static class VanSaleCreditNotes
{
    /// <summary>SAP object type of an A/R invoice, as a credit note line's <c>BaseType</c> carries it.</summary>
    public const int InvoiceBaseType = 13;

    /// <summary>The SAP DocEntries of every invoice the van app raised, as a query EF can nest.</summary>
    public static IQueryable<int> VanInvoiceDocEntries(ApplicationDbContext db) =>
        db.StockReservations
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.Status == ReservationStatus.Confirmed
                                  && reservation.SAPDocEntry != null)
            .Select(reservation => reservation.SAPDocEntry!.Value)
            .Concat(db.DesktopSales
                .Where(sale => SaleSourceSystems.VanSaleSources.Contains(sale.SourceSystem!)
                               && sale.SapDocEntry != null)
                .Select(sale => sale.SapDocEntry!.Value));

    /// <summary>
    /// Keeps the projected credit notes that are (<paramref name="vanSalesOnly"/> true) or are not
    /// (false) van credit notes; null leaves the query alone. Applied in SQL, before any count.
    /// </summary>
    public static IQueryable<SapCreditNoteSnapshotEntity> WhereVanSales(
        this IQueryable<SapCreditNoteSnapshotEntity> query,
        ApplicationDbContext db,
        bool? vanSalesOnly)
    {
        if (!vanSalesOnly.HasValue)
            return query;

        var vanInvoices = VanInvoiceDocEntries(db);

        return vanSalesOnly.Value
            ? query.Where(snapshot => snapshot.Lines.Any(line =>
                line.BaseType == InvoiceBaseType
                && line.BaseEntry != null
                && vanInvoices.Contains(line.BaseEntry.Value)))
            : query.Where(snapshot => !snapshot.Lines.Any(line =>
                line.BaseType == InvoiceBaseType
                && line.BaseEntry != null
                && vanInvoices.Contains(line.BaseEntry.Value)));
    }

    /// <summary>
    /// The same filter over local <c>CreditNotes</c> rows, which keep no line base references: their
    /// header's <c>OriginalInvoiceDocEntry</c> is the invoice they reverse.
    /// </summary>
    public static IQueryable<CreditNoteEntity> WhereVanSales(
        this IQueryable<CreditNoteEntity> query,
        ApplicationDbContext db,
        bool? vanSalesOnly)
    {
        if (!vanSalesOnly.HasValue)
            return query;

        var vanInvoices = VanInvoiceDocEntries(db);

        return vanSalesOnly.Value
            ? query.Where(note => note.OriginalInvoiceDocEntry != null
                                  && vanInvoices.Contains(note.OriginalInvoiceDocEntry.Value))
            : query.Where(note => note.OriginalInvoiceDocEntry == null
                                  || !vanInvoices.Contains(note.OriginalInvoiceDocEntry.Value));
    }

    /// <summary>The invoice DocEntries a set of SAP credit notes is based on — what to look up.</summary>
    public static HashSet<int> BaseInvoiceDocEntries(IEnumerable<SAPCreditNote> creditNotes) =>
        creditNotes
            .SelectMany(note => note.DocumentLines ?? [])
            .Where(line => line.BaseType == InvoiceBaseType && line.BaseEntry.HasValue)
            .Select(line => line.BaseEntry!.Value)
            .ToHashSet();

    /// <summary>
    /// Whether a credit note read from SAP is a van credit note, given the van invoice DocEntries
    /// among the invoices it could be based on.
    /// </summary>
    public static bool IsVanCreditNote(SAPCreditNote creditNote, IReadOnlySet<int> vanInvoiceDocEntries) =>
        (creditNote.DocumentLines ?? []).Any(line =>
            line.BaseType == InvoiceBaseType
            && line.BaseEntry.HasValue
            && vanInvoiceDocEntries.Contains(line.BaseEntry.Value));
}
