using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ReportProductGroups.Commands.SaveReportProductGroup;

public sealed class SaveReportProductGroupHandler(
    ApplicationDbContext db,
    ILogger<SaveReportProductGroupHandler> logger)
    : IRequestHandler<SaveReportProductGroupCommand, ErrorOr<ReportProductGroupResult>>
{
    public async Task<ErrorOr<ReportProductGroupResult>> Handle(
        SaveReportProductGroupCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();

        // Compared without case, so "Complimentary Lines" cannot sit beside "Complimentary lines".
        var lowered = name.ToLower();
        var otherId = command.Id ?? 0;
        if (await db.ReportProductGroups.AnyAsync(g => g.Name.ToLower() == lowered && g.Id != otherId, cancellationToken))
        {
            return Errors.ReportProductGroup.DuplicateName(name);
        }

        ReportProductGroupEntity? group;
        if (command.Id is { } id)
        {
            group = await db.ReportProductGroups
                .Include(g => g.Members)
                .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (group is null)
            {
                return Errors.ReportProductGroup.NotFound(id);
            }

            db.ReportProductGroupMembers.RemoveRange(group.Members);
            group.Members.Clear();
        }
        else
        {
            group = new ReportProductGroupEntity { CreatedAt = DateTime.UtcNow };
            db.ReportProductGroups.Add(group);
        }

        group.Name = name;
        group.Description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        group.SortOrder = command.SortOrder;
        group.IsActive = command.IsActive;
        group.UpdatedAt = DateTime.UtcNow;
        group.UpdatedBy = string.IsNullOrWhiteSpace(command.UpdatedBy) ? null : command.UpdatedBy.Trim();

        // Item codes are kept upper-cased, as sale lines carry them, and a member named twice is kept once.
        var members = command.Members
            .Select(m => new ReportProductGroupMemberEntity
            {
                Kind = m.Kind,
                Code = Normalise(m.Kind, m.Code),
                Name = string.IsNullOrWhiteSpace(m.Name) ? null : m.Name.Trim()
            })
            .GroupBy(m => (m.Kind, m.Code))
            .Select(g => g.First());
        group.Members.AddRange(members);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Action} report product group {GroupId} {Name} with {Members} member(s) by {UpdatedBy}",
            command.Id is null ? "Created" : "Updated", group.Id, group.Name, group.Members.Count, group.UpdatedBy ?? "unknown");

        return new ReportProductGroupResult(
            group.Id, group.Name, group.Description, group.SortOrder, group.IsActive, group.CreatedAt, group.UpdatedAt, group.UpdatedBy,
            group.Members
                .OrderBy(m => m.Kind)
                .ThenBy(m => m.Code, StringComparer.Ordinal)
                .Select(m => new ReportProductGroupMemberResult(m.Kind, m.Code, m.Name))
                .ToList());
    }

    private static string Normalise(ReportProductGroupMemberKind kind, string code) =>
        kind == ReportProductGroupMemberKind.ItemGroup
            ? int.Parse(code.Trim(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : code.Trim().ToUpperInvariant();
}
