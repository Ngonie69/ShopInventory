using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ReportProductGroups.Commands.DeleteReportProductGroup;

public sealed record DeleteReportProductGroupCommand(int Id, string? DeletedBy) : IRequest<ErrorOr<Deleted>>;
