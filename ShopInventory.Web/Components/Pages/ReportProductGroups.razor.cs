using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Features.ReportProductGroups;
using ShopInventory.Web.Features.ReportProductGroups.Commands.DeleteReportProductGroup;
using ShopInventory.Web.Features.ReportProductGroups.Commands.SaveReportProductGroup;
using ShopInventory.Web.Features.ReportProductGroups.Queries.GetReportProductGroups;
using ShopInventory.Web.Features.Reports.MonthOnMonth;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// /reports/product-groups: the named product groups the month-on-month report follows. A group holds named
/// items, whole SAP item groups, or both, and is edited as products come and go.
/// </summary>
public partial class ReportProductGroups
{
    [Inject] private IMediator Mediator { get; set; } = null!;
    [Inject] private ILogger<ReportProductGroups> Logger { get; set; } = null!;

    private sealed class Draft
    {
        public int? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public List<ReportProductGroupMemberDto> Members { get; set; } = [];
    }

    private List<ReportProductGroupDto> groups = [];
    private IReadOnlyList<NocturnePickerOption> itemOptions = [];
    private Dictionary<string, string> itemNames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> itemGroupNames = [];

    private Draft? draft;
    private string? pickedItem;
    private string bulkCodes = string.Empty;
    private bool loading = true;
    private bool saving;
    private bool confirmDelete;
    private string? message;
    private string? error;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var master = await Mediator.Send(new GetMonthOnMonthItemMasterQuery());

            itemGroupNames = master.ItemGroupNames.ToDictionary(g => g.Key.ToString(), g => g.Value);
            itemNames = master.Items.ToDictionary(i => i.Key, i => i.Value.ItemName ?? i.Key, StringComparer.OrdinalIgnoreCase);
            // One search for both kinds of member: the whole SAP item groups first, then every item.
            itemOptions = master.ItemGroupNames
                .OrderBy(g => g.Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NocturnePickerOption(GroupValuePrefix + g.Key, $"{g.Value} · every item in the group", $"Whole SAP item group {g.Key}"))
                .Concat(master.Items
                    .OrderBy(i => i.Key, StringComparer.Ordinal)
                    .Select(i => new NocturnePickerOption(
                        i.Key,
                        i.Value.ItemName ?? i.Key,
                        i.Value.ItemsGroupCode is { } g && itemGroupNames.TryGetValue(g.ToString(), out var name) ? $"{i.Key} · {name}" : i.Key)))
                .ToList();

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading the product groups page failed");
            error = "The product groups could not be loaded.";
        }
        finally
        {
            loading = false;
        }
    }

    private async Task LoadAsync()
    {
        var result = await Mediator.Send(new GetReportProductGroupsQuery(IncludeInactive: true));
        if (result.IsError)
        {
            error = result.FirstError.Description;
            return;
        }

        groups = result.Value;
    }

    private void New()
    {
        draft = new Draft { SortOrder = groups.Count == 0 ? 1 : groups.Max(g => g.SortOrder) + 1 };
        confirmDelete = false;
        message = error = null;
    }

    private void Edit(ReportProductGroupDto group)
    {
        draft = new Draft
        {
            Id = group.Id,
            Name = group.Name,
            Description = group.Description,
            SortOrder = group.SortOrder,
            IsActive = group.IsActive,
            Members = group.Members.ToList()
        };
        confirmDelete = false;
        message = error = null;
    }

    private void Cancel()
    {
        draft = null;
        confirmDelete = false;
    }

    private void AddItem(string? code)
    {
        if (draft is null || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var normalised = code.Trim().ToUpperInvariant();
        if (!draft.Members.Any(m => m.Kind == ReportProductGroupMemberKind.Item && m.Code == normalised))
        {
            draft.Members.Add(new ReportProductGroupMemberDto(ReportProductGroupMemberKind.Item, normalised, itemNames.GetValueOrDefault(normalised)));
        }
    }

    /// <summary>Marks a search result that names a whole SAP item group rather than one item.</summary>
    private const string GroupValuePrefix = "group:";

    private Task OnItemPicked()
    {
        if (draft is not null && !string.IsNullOrWhiteSpace(pickedItem))
        {
            if (pickedItem.StartsWith(GroupValuePrefix, StringComparison.Ordinal))
            {
                var number = pickedItem[GroupValuePrefix.Length..];
                if (!draft.Members.Any(m => m.Kind == ReportProductGroupMemberKind.ItemGroup && m.Code == number))
                {
                    draft.Members.Add(new ReportProductGroupMemberDto(
                        ReportProductGroupMemberKind.ItemGroup, number, itemGroupNames.GetValueOrDefault(number)));
                }
            }
            else
            {
                AddItem(pickedItem);
            }
        }

        pickedItem = null;
        return Task.CompletedTask;
    }

    private IEnumerable<ReportProductGroupMemberDto> WholeGroups =>
        draft?.Members.Where(m => m.Kind == ReportProductGroupMemberKind.ItemGroup).OrderBy(m => m.Code) ?? Enumerable.Empty<ReportProductGroupMemberDto>();

    private IEnumerable<ReportProductGroupMemberDto> NamedItems =>
        draft?.Members.Where(m => m.Kind == ReportProductGroupMemberKind.Item).OrderBy(m => m.Code, StringComparer.Ordinal) ?? Enumerable.Empty<ReportProductGroupMemberDto>();

    private string ProductCount
    {
        get
        {
            var whole = WholeGroups.Count();
            var items = NamedItems.Count();
            if (whole + items == 0)
            {
                return "No products yet";
            }

            var parts = new List<string>();
            if (whole > 0)
            {
                parts.Add(whole == 1 ? "1 whole SAP group" : $"{whole} whole SAP groups");
            }

            if (items > 0)
            {
                parts.Add(items == 1 ? "1 named item" : $"{items} named items");
            }

            return string.Join(" + ", parts);
        }
    }

    /// <summary>Adds every code typed or pasted, separated by commas, spaces or new lines.</summary>
    private void AddBulk()
    {
        var codes = bulkCodes.Split([',', ' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = codes.Where(c => !itemNames.ContainsKey(c)).Select(c => c.ToUpperInvariant()).ToList();
        foreach (var code in codes)
        {
            AddItem(code);
        }

        bulkCodes = string.Empty;
        message = unknown.Count == 0
            ? $"Added {codes.Length} code(s)."
            : $"Added {codes.Length} code(s); not in the product master: {string.Join(", ", unknown)}. They will still count if they are sold under those codes.";
    }

    private void Remove(ReportProductGroupMemberDto member) => draft?.Members.Remove(member);

    private async Task SaveAsync()
    {
        if (draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            error = "Give the group a name.";
            return;
        }

        saving = true;
        error = message = null;
        try
        {
            var result = await Mediator.Send(new SaveReportProductGroupCommand(
                draft.Id, draft.Name.Trim(), draft.Description, draft.SortOrder, draft.IsActive, draft.Members));
            if (result.IsError)
            {
                error = result.FirstError.Description;
                return;
            }

            message = $"Saved {result.Value.Name}.";
            await LoadAsync();
            Edit(result.Value);
            message = $"Saved {result.Value.Name}.";
        }
        finally
        {
            saving = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (draft?.Id is not { } id)
        {
            return;
        }

        if (!confirmDelete)
        {
            confirmDelete = true;
            return;
        }

        saving = true;
        try
        {
            var result = await Mediator.Send(new DeleteReportProductGroupCommand(id));
            if (result.IsError)
            {
                error = result.FirstError.Description;
                return;
            }

            message = $"Deleted {draft.Name}.";
            draft = null;
            confirmDelete = false;
            await LoadAsync();
        }
        finally
        {
            saving = false;
        }
    }

    private string Summary(ReportProductGroupDto group)
    {
        var parts = group.Members
            .Where(m => m.Kind == ReportProductGroupMemberKind.ItemGroup)
            .Select(m => $"all of {itemGroupNames.GetValueOrDefault(m.Code) ?? m.Name ?? m.Code}")
            .ToList();
        var items = group.Members.Count(m => m.Kind == ReportProductGroupMemberKind.Item);
        if (items > 0)
        {
            parts.Add(items == 1 ? "1 item" : $"{items} items");
        }

        return parts.Count == 0 ? "No products yet" : string.Join(" + ", parts);
    }

    private string MemberName(ReportProductGroupMemberDto member) =>
        member.Kind == ReportProductGroupMemberKind.ItemGroup
            ? itemGroupNames.GetValueOrDefault(member.Code) ?? member.Name ?? $"Item group {member.Code}"
            : itemNames.GetValueOrDefault(member.Code) ?? member.Name ?? "Not in the product master";
}
