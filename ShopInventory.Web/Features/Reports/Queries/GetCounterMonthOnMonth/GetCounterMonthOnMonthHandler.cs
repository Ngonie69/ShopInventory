using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.Reports.Queries.GetCounterMonthOnMonth;

public sealed class GetCounterMonthOnMonthHandler(
    HttpClient httpClient,
    ILogger<GetCounterMonthOnMonthHandler> logger)
    : IRequestHandler<GetCounterMonthOnMonthQuery, ErrorOr<CounterMonthOnMonthData>>
{
    public async Task<ErrorOr<CounterMonthOnMonthData>> Handle(
        GetCounterMonthOnMonthQuery request, CancellationToken cancellationToken)
    {
        var url = $"api/DesktopIntegration/sales/month-on-month?previousMonth={request.PreviousMonth:yyyy-MM}-01&currentMonth={request.CurrentMonth:yyyy-MM}-01";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProblemDetailReader.ReadMessageAsync(response, cancellationToken);
                logger.LogWarning("Failed to load the month-on-month report from {Url}. Status code: {StatusCode}. Detail: {Detail}",
                    url, (int)response.StatusCode, detail);

                return Errors.MonthOnMonth.LoadFailed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Your session could not read these sales. Refresh and try again.",
                    HttpStatusCode.Forbidden => "This account is not permitted to read these sales.",
                    HttpStatusCode.BadRequest when detail is not null => detail,
                    _ => "Failed to load the month-on-month figures."
                });
            }

            var data = await response.Content.ReadFromJsonAsync<CounterMonthOnMonthData>(cancellationToken: cancellationToken);
            return data is null
                ? Errors.MonthOnMonth.LoadFailed("Failed to load the month-on-month figures.")
                : data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading the month-on-month report from {Url}", url);
            return Errors.MonthOnMonth.LoadFailed("Failed to load the month-on-month figures.");
        }
    }
}
