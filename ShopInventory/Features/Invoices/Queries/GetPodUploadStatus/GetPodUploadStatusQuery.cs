using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

public sealed record GetPodUploadStatusQuery(DateTime FromDate, DateTime ToDate) : IRequest<ErrorOr<PodUploadStatusReportDto>>;
