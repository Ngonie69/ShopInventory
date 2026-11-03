using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.ReportProductGroups.Commands.DeleteReportProductGroup;
using ShopInventory.Features.ReportProductGroups.Commands.SaveReportProductGroup;
using ShopInventory.Features.ReportProductGroups.Queries.GetReportProductGroups;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The product groups a month-on-month report follows: named lists of items (or whole SAP item groups)
/// that administrators change as products come and go.
/// </summary>
public sealed class ReportProductGroupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ReportProductGroupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private SaveReportProductGroupHandler Save() => new(_context, NullLogger<SaveReportProductGroupHandler>.Instance);

    private static SaveReportProductGroupCommand Command(
        int? id, string name, params SaveReportProductGroupMember[] members) =>
        new(id, name, null, 1, true, members, "ngoni");

    private static SaveReportProductGroupMember Item(string code) => new(ReportProductGroupMemberKind.Item, code, null);

    private static SaveReportProductGroupMember Group(string number) => new(ReportProductGroupMemberKind.ItemGroup, number, null);

    [Fact]
    public async Task A_new_group_keeps_item_codes_upper_cased_and_each_member_once()
    {
        var result = await Save().Handle(Command(null, "Shamiso new flavours", Item("ics024"), Item("ICS024 "), Item("ICS025")), default);

        Assert.False(result.IsError);
        Assert.Equal(["ICS024", "ICS025"], result.Value.Members.Select(m => m.Code));
        Assert.Equal("ngoni", result.Value.UpdatedBy);
    }

    [Fact]
    public async Task An_item_group_member_is_stored_as_its_plain_number()
    {
        var result = await Save().Handle(Command(null, "Complimentary lines", Group("0150")), default);

        var member = Assert.Single(result.Value.Members);
        Assert.Equal(ReportProductGroupMemberKind.ItemGroup, member.Kind);
        Assert.Equal("150", member.Code);
    }

    [Fact]
    public async Task Saving_a_group_again_replaces_its_whole_member_list()
    {
        var created = await Save().Handle(Command(null, "Range", Item("A1"), Item("B2")), default);

        var updated = await Save().Handle(Command(created.Value.Id, "Range renamed", Item("B2"), Item("C3")), default);

        Assert.Equal("Range renamed", updated.Value.Name);
        Assert.Equal(["B2", "C3"], updated.Value.Members.Select(m => m.Code));
        Assert.Equal(2, await _context.ReportProductGroupMembers.CountAsync());
    }

    [Fact]
    public async Task A_second_group_cannot_take_a_name_already_used_whatever_its_case()
    {
        await Save().Handle(Command(null, "Complimentary lines", Item("A1")), default);

        var clash = await Save().Handle(Command(null, "COMPLIMENTARY LINES", Item("B2")), default);

        Assert.True(clash.IsError);
        Assert.Equal("ReportProductGroup.DuplicateName", clash.FirstError.Code);
    }

    [Fact]
    public async Task A_group_may_keep_its_own_name_when_saved_again()
    {
        var created = await Save().Handle(Command(null, "Range", Item("A1")), default);

        var again = await Save().Handle(Command(created.Value.Id, "Range", Item("A1")), default);

        Assert.False(again.IsError);
    }

    [Fact]
    public async Task Saving_a_group_that_does_not_exist_is_not_found()
    {
        var result = await Save().Handle(Command(999, "Ghost", Item("A1")), default);

        Assert.Equal("ReportProductGroup.NotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task Deleting_a_group_removes_its_members_too()
    {
        var created = await Save().Handle(Command(null, "Range", Item("A1"), Item("B2")), default);

        var deleted = await new DeleteReportProductGroupHandler(_context, NullLogger<DeleteReportProductGroupHandler>.Instance)
            .Handle(new DeleteReportProductGroupCommand(created.Value.Id, "ngoni"), default);

        Assert.False(deleted.IsError);
        Assert.Empty(_context.ReportProductGroups);
        Assert.Empty(_context.ReportProductGroupMembers);
    }

    [Fact]
    public async Task Retired_groups_are_listed_only_when_asked_for()
    {
        await Save().Handle(Command(null, "Live", Item("A1")), default);
        await Save().Handle(new SaveReportProductGroupCommand(null, "Retired", null, 2, false, [Item("B2")], "ngoni"), default);

        var handler = new GetReportProductGroupsHandler(_context);
        var active = await handler.Handle(new GetReportProductGroupsQuery(IncludeInactive: false), default);
        var all = await handler.Handle(new GetReportProductGroupsQuery(IncludeInactive: true), default);

        Assert.Equal(["Live"], active.Value.Select(g => g.Name));
        Assert.Equal(["Live", "Retired"], all.Value.Select(g => g.Name));
    }

    [Fact]
    public void An_item_group_member_must_be_a_number()
    {
        var validator = new SaveReportProductGroupValidator();

        var result = validator.Validate(Command(null, "Bad", Group("Complimentary")));

        Assert.False(result.IsValid);
    }
}
