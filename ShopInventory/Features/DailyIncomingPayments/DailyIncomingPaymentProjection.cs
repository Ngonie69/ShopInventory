using System.Linq.Expressions;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DailyIncomingPayments;

internal static class DailyIncomingPaymentProjection
{
    /// <summary>
    /// Translates to SQL, so a list reads the header and a line count, not every line. The status stays an
    /// enum here and becomes a name in <see cref="ToDto"/>.
    /// </summary>
    public static readonly Expression<Func<DailyIncomingPaymentEntity, SummaryRow>> Summary =
        payment => new SummaryRow(
            payment.Id,
            payment.Reference,
            payment.CardCode,
            payment.CardName,
            payment.PaymentDate,
            payment.Status,
            payment.CashSum,
            payment.TransferSum + payment.CreditSum,
            payment.CashAccount,
            payment.TransferAccount,
            payment.SapDocNum,
            payment.PostedAtUtc,
            payment.Lines.Count,
            payment.LastError,
            payment.EmailSentAtUtc,
            payment.EmailError);

    public static DailyIncomingPaymentSummaryDto ToDto(SummaryRow row) =>
        new(
            row.Id,
            row.Reference,
            row.CardCode,
            row.CardName,
            row.PaymentDate,
            row.Status.ToString(),
            row.CashSum,
            row.ElectronicSum,
            row.CashAccount,
            row.TransferAccount,
            row.SapDocNum,
            row.PostedAtUtc,
            row.InvoiceCount,
            row.LastError,
            row.EmailSentAtUtc,
            row.EmailError);

    internal sealed record SummaryRow(
        int Id,
        string Reference,
        string CardCode,
        string? CardName,
        DateTime PaymentDate,
        DailyIncomingPaymentStatus Status,
        decimal CashSum,
        decimal ElectronicSum,
        string? CashAccount,
        string? TransferAccount,
        int? SapDocNum,
        DateTime? PostedAtUtc,
        int InvoiceCount,
        string? LastError,
        DateTime? EmailSentAtUtc,
        string? EmailError);
}
