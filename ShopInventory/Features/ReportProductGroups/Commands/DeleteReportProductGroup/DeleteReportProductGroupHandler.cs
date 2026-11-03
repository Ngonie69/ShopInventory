using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.ReportProductGroups.Commands.DeleteReportProductGroup;

public sealed class DeleteReportProductGroupHandler(
    ApplicationDbContext db,
    ILogger<DeleteReportProductGroupHandler> logger)
    : IRequestHandler<DeleteReportProductGroupCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(DeleteReportProductGroupCommand command, CancellationToken cancellationToken)
    {
        var group = await db.ReportProductGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == command.Id, cancellationToken);
        if (group is null)
        {
            return Errors.ReportProductGroup.NotFound(command.Id);
        }

        db.ReportProductGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted report product group {GroupId} {Name} by {DeletedBy}",
            group.Id, group.Name, command.DeletedBy ?? "unknown");
        return Result.Deleted;
    }
}
