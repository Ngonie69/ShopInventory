using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesPodDeliveries;

public sealed class GetVanSalesPodDeliveriesHandler(
    ApplicationDbContext db,
    IMediator mediator,
    ILogger<GetVanSalesPodDeliveriesHandler> logger
) : IRequestHandler<GetVanSalesPodDeliveriesQuery, ErrorOr<VanSalesPodDeliveriesDto>>
{
    public async Task<ErrorOr<VanSalesPodDeliveriesDto>> Handle(
        GetVanSalesPodDeliveriesQuery request,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.UserId)
            .Select(candidate => new { candidate.Username, candidate.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var fromDate = request.FromDate.Date;
        var toDate = request.ToDate.Date;

        var shopList = await DriverShopList.FindAsync(db, excludingUserId: null, cancellationToken);
        if (shopList is null)
        {
            logger.LogInformation(
                "No shops are on the drivers' shop list; {Username} is shown no deliveries",
                user.Username);

            return new VanSalesPodDeliveriesDto
            {
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                ShopCount = 0
            };
        }

        // The same report the portal's POD pages read, and the same cache, so a van and the office see
        // one answer about which invoices still owe a note.
        var report = await mediator.Send(
            new GetPodUploadStatusQuery(
                fromDate,
                toDate,
                UserId: null,
                CustomerCodeScope: shopList.CustomerCodes),
            cancellationToken);

        if (report.IsError)
        {
            return report.Errors;
        }

        return ToDeliveries(report.Value, shopList.CustomerCodes.Count);
    }

    internal static VanSalesPodDeliveriesDto ToDeliveries(PodUploadStatusReportDto report, int shopCount) => new()
    {
        FromDate = report.FromDate,
        ToDate = report.ToDate,
        ShopCount = shopCount,
        CreditNoteDataComplete = report.CreditNoteDataComplete,

        // The report holds credited invoices apart so they do not count against its completion figure.
        // They are folded back in here, flagged: a rep holding the paper for one should find it and be
        // told it was credited, not be left looking for an invoice that has vanished.
        Invoices = report.Items
            .Concat(report.FullyCreditedItems)
            .GroupBy(item => item.DocEntry)
            .Select(group => group.First())
            .OrderByDescending(item => item.DocDate, StringComparer.Ordinal)
            .ThenByDescending(item => item.DocNum)
            .Select(item => new VanSalesPodDeliveryDto
            {
                DocEntry = item.DocEntry,
                DocNum = item.DocNum,
                DocDate = item.DocDate,
                CardCode = item.CardCode,
                CardName = item.CardName,
                DocTotal = item.DocTotal,
                DocCurrency = item.DocCurrency,
                HasPod = item.HasPod,
                PodCount = item.PodCount,
                PodUploadedAt = item.PodUploadedAt,
                IsFullyCredited = item.IsFullyCredited,
                CreditNoteNumber = item.CreditNoteNumber,
                Uploaders = item.PodUploadedByUsers
                    .Select(uploader => new VanSalesPodUploaderDto
                    {
                        Username = uploader.Username,
                        Role = uploader.Role,
                        FileCount = uploader.FileCount,
                        LatestUploadedAt = uploader.LatestUploadedAt
                    })
                    .ToList()
            })
            .ToList()
    };
}
