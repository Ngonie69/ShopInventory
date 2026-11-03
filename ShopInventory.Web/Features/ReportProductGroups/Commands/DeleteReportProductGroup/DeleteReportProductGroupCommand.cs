using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ReportProductGroups.Commands.DeleteReportProductGroup;

public sealed record DeleteReportProductGroupCommand(int Id) : IRequest<ErrorOr<Deleted>>;
