using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;

/// <summary>
/// Emails management how shops, vending and the vans traded — every figure in the message, with the Shop
/// &amp; Vending review attached as a PDF.
/// </summary>
/// <param name="Cadence">daily, weekly or monthly for the last complete period; custom for <paramref name="FromDate"/> to <paramref name="ToDate"/>.</param>
/// <param name="Scheduled">
/// True from the job: the schedule's switches, recipients and read-as account apply, and a period already
/// sent is not sent again. False from a person: their own scope, the recipients they name, sent now.
/// </param>
/// <param name="CallerUserId">Whose scope the shop and vending figures are read under; null from the job, which reads as the schedule's admin.</param>
/// <param name="CallerName">Who sent it, for the audit trail.</param>
/// <param name="FromDate">First day of a custom period.</param>
/// <param name="ToDate">Last day of a custom period; defaults to its first.</param>
/// <param name="Recipients">Who to send to; empty means the schedule's list.</param>
public sealed record SendSalesPerformanceEmailCommand(
    string Cadence,
    bool Scheduled,
    Guid? CallerUserId = null,
    string? CallerName = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    List<string>? Recipients = null
) : IRequest<ErrorOr<SalesPerformanceEmailResult>>;
