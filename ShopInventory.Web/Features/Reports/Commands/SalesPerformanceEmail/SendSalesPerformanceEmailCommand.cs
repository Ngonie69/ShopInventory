using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

namespace ShopInventory.Web.Features.Reports.Commands.SalesPerformanceEmail;

/// <summary>Emails the management sales performance now: yesterday, the last complete week or month, or the period given.</summary>
public sealed record SendSalesPerformanceEmailCommand(string Cadence, DateTime? FromDate, DateTime? ToDate, List<string> Recipients)
    : IRequest<ErrorOr<SalesPerformanceEmailResult>>;
