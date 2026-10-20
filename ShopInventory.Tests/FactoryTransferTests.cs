using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.FactoryTransfers;
using ShopInventory.Features.FactoryTransfers.Commands.CancelFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.DecideFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.RecallFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.RetryFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.SendFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;
using ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Production releases stock to dispatch, and dispatch returns it, through FSRW. Each side may act only
/// on its own warehouses, nobody accepts their own transfer, and whatever is not accepted goes back
/// in the same SAP document. A post whose reply was lost is found by its order number, never repeated.
/// </summary>
public sealed class FactoryTransferTests : IDisposable
{
    // Named after the real people on the sheet the module was designed from.
    private static readonly Guid Eunice = Guid.Parse("10000000-0000-0000-0000-000000000001");   // yoghurt + milk production
    private static readonly Guid Daniel = Guid.Parse("10000000-0000-0000-0000-000000000002");   // cheese production
    private static readonly Guid Leo = Guid.Parse("20000000-0000-0000-0000-000000000001");      // yoghurt dispatch
    private static readonly Guid Garikai = Guid.Parse("20000000-0000-0000-0000-000000000002");  // milk + cheese dispatch
    private static readonly Guid Millicent = Guid.Parse("20000000-0000-0000-0000-000000000003"); // dispatch manager
    private static readonly Guid Douglas = Guid.Parse("30000000-0000-0000-0000-000000000001");   // distribution
    private static readonly Guid Graniteside = Guid.Parse("40000000-0000-0000-0000-000000000001"); // depot controller
    private static readonly Guid Machipisa = Guid.Parse("40000000-0000-0000-0000-000000000002");   // depot controller

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly FakeSap _sap = new();
    private int _requests;

    public FactoryTransferTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            NewUser(Eunice, "eunice", ApplicationRoles.ProductionOfficer, "Eunice", "Shabani", ["PROYG", "PROYG 2", "PROMM"]),
            NewUser(Daniel, "daniel", ApplicationRoles.ProductionOfficer, "Daniel", "Mudzudzu", ["PROCH", "PROPC"]),
            NewUser(Leo, "leo", ApplicationRoles.DispatchOfficer, "Leo", "Banda", ["DISPYOG"]),
            NewUser(Garikai, "garikai", ApplicationRoles.DispatchOfficer, "Garikai", "Kuseni", ["DISPMILK", "DISPCH"]),
            NewUser(Millicent, "millicent", ApplicationRoles.DispatchOfficer, "Millicent", "Muchechetere", [],
                [Permission.AcceptAnyFactoryTransfers]),
            NewUser(Douglas, "douglas", ApplicationRoles.DistributionOfficer, "Douglas", "Makosa", ["DISPYOG", "DISPMILK", "DISPICE", "DISPCH"]),
            NewUser(Graniteside, "graniteside", ApplicationRoles.DepotController, "Graniteside", "Controller", ["KEFGRC", "KEFGRS"]),
            NewUser(Machipisa, "machipisa", ApplicationRoles.DepotController, "Machipisa", "Controller", ["CORMACH", "CORMACH2"]));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- Sending -----------------------------------------------------------------------------

    [Fact]
    public async Task A_release_moves_the_stock_into_FSRW_stamped_for_dispatch()
    {
        var result = await Send(Eunice, "PROYG", null, "End of shift 2", ("YOG500", 20m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var saved = await ReloadAsync(result.Value.Transfer.Summary.Id);
        Assert.Equal(FactoryTransferStatuses.InTransit, saved.Status);
        Assert.Equal(FactoryTransferDirections.Release, saved.Direction);
        Assert.Equal("DISPYOG", saved.DestinationWarehouseCode);
        Assert.Equal(FakeSap.FirstDocNum, saved.OutSapDocNum);

        var posted = Assert.Single(_sap.Created);
        Assert.Equal("PROYG", posted.FromWarehouse);
        Assert.Equal("FSRW", posted.ToWarehouse);
        Assert.Equal("FT-000001-OUT", posted.OrderNumber);
        Assert.Equal("DISPYOG", posted.FinalDestinationWarehouse);
        Assert.Contains("Sent by Eunice Shabani", posted.Comments);
        Assert.Contains("Reason: End of shift 2", posted.Comments);

        // The batches SAP chose are kept, so the in leg moves exactly those out of FSRW.
        var batches = FactoryTransferLegPoster.ReadBatches(saved.Lines.Single());
        Assert.Equal([new FactoryTransferBatch("YOG500-B1", 20m)], batches);
    }

    [Fact]
    public async Task A_production_officer_cannot_send_from_a_warehouse_that_is_not_theirs()
    {
        var result = await Send(Eunice, "PROIC", null, "Not mine", ("ICE1L", 5m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.SourceNotAssigned", result.FirstError.Code);
        Assert.Empty(_sap.Created);
        Assert.Equal(0, await _context.FactoryTransfers.CountAsync());
    }

    [Fact]
    public async Task Yoghurt_cannot_be_released_into_another_departments_dispatch()
    {
        var result = await Send(Eunice, "PROYG", "DISPCH", "Wrong door", ("YOG500", 5m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.InvalidDestination", result.FirstError.Code);
        Assert.Empty(_sap.Created);
    }

    [Fact]
    public async Task A_return_names_which_production_warehouse_when_the_department_has_two()
    {
        var unnamed = await Send(Leo, "DISPYOG", null, "Export rework", ("YOG500", 4m));
        Assert.True(unnamed.IsError);
        Assert.Equal("FactoryTransfer.InvalidDestination", unnamed.FirstError.Code);

        var named = await Send(Leo, "DISPYOG", "proyg 2", "Export rework", ("YOG500", 4m));
        Assert.False(named.IsError, named.IsError ? named.FirstError.Description : null);
        Assert.Equal(FactoryTransferDirections.Return, named.Value.Transfer.Summary.Direction);
        Assert.Equal("PROYG 2", named.Value.Transfer.Summary.DestinationWarehouseCode);
        Assert.Equal("DISPYOG", _sap.Created.Single().FromWarehouse);
    }

    [Fact]
    public async Task A_resent_form_answers_with_the_transfer_it_already_made()
    {
        var first = await Send(Eunice, "PROYG", null, "Shift 1", "form-1", ("YOG500", 3m));
        var second = await Send(Eunice, "PROYG", null, "Shift 1", "form-1", ("YOG500", 3m));

        Assert.False(second.IsError);
        Assert.Equal(first.Value.Transfer.Summary.Id, second.Value.Transfer.Summary.Id);
        Assert.Single(_sap.Created);
    }

    [Fact]
    public async Task A_refused_send_is_kept_as_failed_and_can_be_cancelled_because_nothing_moved()
    {
        _sap.RefuseNext = new InvalidOperationException("not enough stock");
        var result = await Send(Eunice, "PROYG", null, "Shift 1", ("YOG500", 3m));

        Assert.True(result.IsError);
        Assert.Contains("FT-000001 was saved but not sent", result.FirstError.Description);
        var id = (await _context.FactoryTransfers.AsNoTracking().SingleAsync()).Id;
        Assert.Equal(FactoryTransferStatuses.SendFailed, (await ReloadAsync(id)).Status);

        var cancelled = await Cancel(Eunice, id);
        Assert.False(cancelled.IsError, cancelled.IsError ? cancelled.FirstError.Description : null);
        Assert.Equal(FactoryTransferStatuses.Cancelled, (await ReloadAsync(id)).Status);
    }

    // ---- Lost replies ------------------------------------------------------------------------

    [Fact]
    public async Task A_send_whose_reply_was_lost_is_found_on_retry_not_posted_twice()
    {
        _sap.LoseNextReply = true;
        var result = await Send(Eunice, "PROYG", null, "Shift 1", ("YOG500", 3m));
        Assert.True(result.IsError);
        var id = (await _context.FactoryTransfers.AsNoTracking().SingleAsync()).Id;
        Assert.Equal(FactoryTransferStatuses.SendFailed, (await ReloadAsync(id)).Status);

        var retried = await Retry(Eunice, id);

        Assert.False(retried.IsError, retried.IsError ? retried.FirstError.Description : null);
        Assert.Single(_sap.Created);
        var saved = await ReloadAsync(id);
        Assert.Equal(FactoryTransferStatuses.InTransit, saved.Status);
        Assert.Equal(FakeSap.FirstDocNum, saved.OutSapDocNum);
        Assert.NotEmpty(FactoryTransferLegPoster.ReadBatches(saved.Lines.Single()));
    }

    [Fact]
    public async Task Cancelling_a_send_that_reached_SAP_after_all_finishes_it_into_transit_instead()
    {
        _sap.LoseNextReply = true;
        await Send(Eunice, "PROYG", null, "Shift 1", ("YOG500", 3m));
        var id = (await _context.FactoryTransfers.AsNoTracking().SingleAsync()).Id;

        var cancelled = await Cancel(Eunice, id);

        Assert.False(cancelled.IsError, cancelled.IsError ? cancelled.FirstError.Description : null);
        Assert.Contains("was not cancelled", cancelled.Value.Message);
        Assert.Equal(FactoryTransferStatuses.InTransit, (await ReloadAsync(id)).Status);
        Assert.Single(_sap.Created);
    }

    // ---- Deciding ----------------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_accepts_the_whole_release_into_its_warehouse()
    {
        var id = await GivenReleaseAsync(("YOG500", 20m));

        var result = await Decide(Leo, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 20m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var saved = await ReloadAsync(id);
        Assert.Equal(FactoryTransferStatuses.Accepted, saved.Status);
        Assert.Equal("Leo Banda", saved.DecidedByName);

        var inLeg = _sap.Created[1];
        Assert.Equal("FSRW", inLeg.FromWarehouse);
        Assert.Equal("DISPYOG", inLeg.ToWarehouse);
        Assert.Equal("FT-000001-IN", inLeg.OrderNumber);
        var line = Assert.Single(inLeg.Lines!);
        Assert.Equal("DISPYOG", line.ToWarehouseCode);
        Assert.Equal("YOG500-B1", Assert.Single(line.BatchNumbers!).BatchNumber);
        Assert.Contains("Sent by Eunice Shabani", inLeg.Comments);
        Assert.Contains("Accepted by Leo Banda", inLeg.Comments);
    }

    [Fact]
    public async Task A_short_acceptance_needs_a_reason_and_sends_the_rest_back_in_the_same_document()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));

        var noReason = await Decide(Leo, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 7m));
        Assert.True(noReason.IsError);
        Assert.Equal("FactoryTransfer.ShortAcceptNeedsReason", noReason.FirstError.Code);

        var result = await Decide(Leo, id, DecideFactoryTransferCommand.Accept, "3 crates leaking", Accepted(id, 7m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(FactoryTransferStatuses.PartiallyAccepted, (await ReloadAsync(id)).Status);

        var inLeg = Assert.Single(_sap.Created.Skip(1));
        Assert.Collection(inLeg.Lines!,
            accepted =>
            {
                Assert.Equal("DISPYOG", accepted.ToWarehouseCode);
                Assert.Equal(7m, accepted.Quantity);
                Assert.Equal(7m, accepted.BatchNumbers!.Sum(batch => batch.Quantity));
            },
            returned =>
            {
                Assert.Equal("PROYG", returned.ToWarehouseCode);
                Assert.Equal(3m, returned.Quantity);
                Assert.Equal(3m, returned.BatchNumbers!.Sum(batch => batch.Quantity));
            });
        Assert.Contains("Part-accepted by Leo Banda", inLeg.Comments);
        Assert.Contains("Note: 3 crates leaking", inLeg.Comments);
    }

    [Fact]
    public async Task A_rejection_sends_everything_back_to_production()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));

        var result = await Decide(Leo, id, DecideFactoryTransferCommand.Reject, "Wrong labels", []);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(FactoryTransferStatuses.Rejected, (await ReloadAsync(id)).Status);
        var line = Assert.Single(_sap.Created[1].Lines!);
        Assert.Equal("PROYG", line.ToWarehouseCode);
        Assert.Equal(10m, line.Quantity);
    }

    [Fact]
    public async Task Dispatch_for_another_department_cannot_accept_yoghurt()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));

        var result = await Decide(Garikai, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 10m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.NotFound", result.FirstError.Code);
        Assert.Equal(FactoryTransferStatuses.InTransit, (await ReloadAsync(id)).Status);
    }

    [Fact]
    public async Task The_dispatch_manager_accepts_for_any_department()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));

        var result = await Decide(Millicent, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 10m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("Millicent Muchechetere", (await ReloadAsync(id)).DecidedByName);
    }

    [Fact]
    public async Task Nobody_accepts_a_transfer_they_sent_themselves()
    {
        // The dispatch manager can accept anything, but not what she sent herself.
        _context.Users.Single(user => user.Id == Millicent).SetWarehouseCodes(["DISPYOG"]);
        await _context.SaveChangesAsync();
        var sent = await Send(Millicent, "DISPYOG", "PROYG", "Rework", ("YOG500", 2m));
        Assert.False(sent.IsError, sent.IsError ? sent.FirstError.Description : null);
        var id = sent.Value.Transfer.Summary.Id;

        var result = await Decide(Millicent, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 2m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.CannotDecideOwnTransfer", result.FirstError.Code);
    }

    [Fact]
    public async Task A_transfer_is_settled_once_however_many_people_answer_it()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));
        await Decide(Leo, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 10m));

        var second = await Decide(Millicent, id, DecideFactoryTransferCommand.Reject, "Too late", []);

        Assert.True(second.IsError);
        Assert.Equal("FactoryTransfer.NotActionable", second.FirstError.Code);
        Assert.Equal(2, _sap.Created.Count);
    }

    [Fact]
    public async Task Accepting_more_than_was_sent_is_refused()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));

        var result = await Decide(Leo, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 11m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.LinesMismatch", result.FirstError.Code);
    }

    [Fact]
    public async Task A_settlement_whose_reply_was_lost_is_found_on_retry()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));
        _sap.LoseNextReply = true;
        var lost = await Decide(Leo, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 10m));
        Assert.True(lost.IsError);
        Assert.Equal(FactoryTransferStatuses.SettleFailed, (await ReloadAsync(id)).Status);

        var retried = await Retry(Leo, id);

        Assert.False(retried.IsError, retried.IsError ? retried.FirstError.Description : null);
        Assert.Equal(FactoryTransferStatuses.Accepted, (await ReloadAsync(id)).Status);
        Assert.Equal(2, _sap.Created.Count);
    }

    // ---- Recalling ---------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_sender_recalls_and_the_stock_goes_back()
    {
        var id = await GivenReleaseAsync(("YOG500", 10m));

        var notSender = await Recall(Leo, id, "Mine now");
        Assert.True(notSender.IsError);
        Assert.Equal("FactoryTransfer.OnlySenderMayRecall", notSender.FirstError.Code);

        var recalled = await Recall(Eunice, id, "Sent the wrong product");
        Assert.False(recalled.IsError, recalled.IsError ? recalled.FirstError.Description : null);
        Assert.Equal(FactoryTransferStatuses.Recalled, (await ReloadAsync(id)).Status);
        var line = Assert.Single(_sap.Created[1].Lines!);
        Assert.Equal("PROYG", line.ToWarehouseCode);
        Assert.Contains("Recalled by Eunice Shabani", _sap.Created[1].Comments);
    }

    // ---- Reading -----------------------------------------------------------------------------

    [Fact]
    public async Task Each_side_sees_only_its_own_warehouses_and_its_queue()
    {
        await GivenReleaseAsync(("YOG500", 10m));
        var cheese = await Send(Daniel, "PROCH", null, "Block cheese", ("CHE1KG", 5m));
        Assert.False(cheese.IsError, cheese.IsError ? cheese.FirstError.Description : null);

        var leo = await List(Leo, GetFactoryTransfersQuery.All);
        var leoItem = Assert.Single(leo.Items);
        Assert.Equal("DISPYOG", leoItem.DestinationWarehouseCode);
        Assert.True(leoItem.CanDecide);
        Assert.Equal(1, leo.AwaitingMeCount);

        var garikai = await List(Garikai, GetFactoryTransfersQuery.Awaiting);
        Assert.Equal("DISPCH", Assert.Single(garikai.Items).DestinationWarehouseCode);

        var eunice = await List(Eunice, GetFactoryTransfersQuery.All);
        var mine = Assert.Single(eunice.Items);
        Assert.False(mine.CanDecide);
        Assert.True(mine.CanRecall);
        Assert.Equal(0, eunice.AwaitingMeCount);

        Assert.Equal(2, (await List(Millicent, GetFactoryTransfersQuery.All)).Items.Count);
    }

    [Fact]
    public async Task The_send_form_offers_exactly_the_routes_the_server_allows()
    {
        var options = await new GetFactoryTransferOptionsHandler(_context, Users())
            .Handle(new GetFactoryTransferOptionsQuery(Eunice), default);

        Assert.False(options.IsError);
        Assert.Equal(
            ["PROYG→DISPYOG", "PROYG 2→DISPYOG", "PROMM→DISPMILK"],
            options.Value.Routes.Select(route => $"{route.SourceWarehouseCode}→{string.Join(",", route.DestinationWarehouseCodes)}"));
    }

    [Fact]
    public void The_remarks_keep_the_audit_trail_when_the_reason_is_long()
    {
        var transfer = new FactoryTransferEntity
        {
            Id = 42,
            DepartmentCode = "YOG",
            Direction = FactoryTransferDirections.Release,
            SourceWarehouseCode = "PROYG",
            DestinationWarehouseCode = "DISPYOG",
            SentByName = "Eunice Shabani",
            SentAtUtc = new DateTime(2026, 10, 20, 12, 2, 0, DateTimeKind.Utc),
            Reason = new string('x', 400),
            Decision = FactoryTransferDecisions.Accepted,
            DecidedByName = "Leo Banda",
            DecidedAtUtc = new DateTime(2026, 10, 20, 12, 31, 0, DateTimeKind.Utc),
            DecisionReason = "short",
            Lines = [new FactoryTransferLineEntity { Quantity = 5, AcceptedQuantity = 4 }]
        };

        var remarks = FactoryTransferRemarks.ForInLeg(transfer);

        Assert.True(remarks.Length <= FactoryTransferRemarks.MaxLength);
        Assert.StartsWith("FT-000042 Yoghurt release PROYG to DISPYOG | Sent by Eunice Shabani 20 Oct 2026 14:02 | Part-accepted by Leo Banda 20 Oct 2026 14:31", remarks);
        Assert.Contains("Reason: xxx", remarks);
    }

    [Fact]
    public void Every_factory_warehouse_belongs_to_exactly_one_department_and_none_is_the_holding_warehouse()
    {
        var all = FactoryDepartments.AllWarehouses.ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(FactoryDepartments.HoldingWarehouseCode, all, StringComparer.OrdinalIgnoreCase);
        // U_ToWhs on a SAP transfer holds eight characters.
        Assert.All(all, code => Assert.True(code.Length <= 8, code));
    }

    // ---- Depot deliveries --------------------------------------------------------------------

    [Fact]
    public async Task Distribution_loads_one_truck_from_several_dispatch_warehouses_into_DEPOTRAN()
    {
        var result = await Deliver(Douglas, "KEFGRC", "ace 1234", "Waiti", null,
            ("DISPYOG", "YOG500", 40m), ("DISPICE", "ICE1L", 12m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var saved = await ReloadAsync(result.Value.Transfer.Summary.Id);
        Assert.Equal(FactoryTransferDirections.Delivery, saved.Direction);
        Assert.Equal("DEPOTRAN", saved.HoldingWarehouseCode);
        Assert.Equal("KEFGRC", saved.DestinationWarehouseCode);
        Assert.Equal(FactoryTransferStatuses.InTransit, saved.Status);

        var posted = Assert.Single(_sap.Created);
        Assert.Equal("DEPOTRAN", posted.ToWarehouse);
        Assert.Equal("KEFGRC", posted.FinalDestinationWarehouse);
        Assert.Equal("ACE 1234", posted.VehiclePlate);
        Assert.Equal(["DISPYOG", "DISPICE"], posted.Lines!.Select(line => line.FromWarehouseCode));
        Assert.All(posted.Lines!, line => Assert.Equal("DEPOTRAN", line.ToWarehouseCode));
        Assert.StartsWith("FT-000001 Graniteside delivery DISPICE+DISPYOG to KEFGRC | Truck ACE 1234 DR Waiti | Sent by Douglas Makosa", posted.Comments);

        Assert.Equal("Graniteside", result.Value.Transfer.Summary.DepartmentName);
        Assert.Equal("DISPYOG, DISPICE", result.Value.Transfer.Summary.SourceLabel);
    }

    [Fact]
    public async Task Only_the_destination_depots_people_are_told_a_truck_is_coming()
    {
        await Deliver(Douglas, "KEFGRC", "ACE1234", "Waiti", null, ("DISPYOG", "YOG500", 40m));

        var told = Assert.Single(_notifications);
        Assert.Equal("graniteside", told.TargetUsername);
        Assert.Equal("TransferApproval", told.Category);
        Assert.Contains("truck ACE1234 (Waiti)", told.Message);
    }

    [Fact]
    public async Task A_truck_needs_its_registration_and_driver()
    {
        var result = await Deliver(Douglas, "KEFGRC", null, "Waiti", null, ("DISPYOG", "YOG500", 40m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.TruckDetailsRequired", result.FirstError.Code);
        Assert.Empty(_sap.Created);
    }

    [Fact]
    public async Task A_dispatch_officer_cannot_load_a_depot_truck()
    {
        var result = await Deliver(Leo, "KEFGRC", "ACE1234", "Waiti", null, ("DISPYOG", "YOG500", 40m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.DeliverNotPermitted", result.FirstError.Code);
    }

    [Fact]
    public async Task Distribution_cannot_load_from_a_warehouse_that_is_not_dispatch()
    {
        var result = await Deliver(Douglas, "KEFGRC", "ACE1234", "Waiti", null, ("PROYG", "YOG500", 40m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.NotADispatchWarehouse", result.FirstError.Code);
    }

    [Fact]
    public async Task The_depot_counts_short_and_each_short_line_goes_back_to_its_own_dispatch_warehouse()
    {
        var sent = await Deliver(Douglas, "KEFGRC", "ACE1234", "Waiti", null,
            ("DISPYOG", "YOG500", 40m), ("DISPICE", "ICE1L", 12m));
        var id = sent.Value.Transfer.Summary.Id;

        var result = await Decide(Graniteside, id, DecideFactoryTransferCommand.Accept, "2 ice cream tubs melted",
            Accepted(id, 40m, 10m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(FactoryTransferStatuses.PartiallyAccepted, (await ReloadAsync(id)).Status);

        var inLeg = _sap.Created[1];
        Assert.Equal("DEPOTRAN", inLeg.FromWarehouse);
        Assert.Equal("ACE1234", inLeg.VehiclePlate);
        Assert.Collection(inLeg.Lines!,
            yoghurt => Assert.Equal(("KEFGRC", 40m), (yoghurt.ToWarehouseCode, yoghurt.Quantity)),
            iceAccepted => Assert.Equal(("KEFGRC", 10m), (iceAccepted.ToWarehouseCode, iceAccepted.Quantity)),
            iceShort => Assert.Equal(("DISPICE", 2m), (iceShort.ToWarehouseCode, iceShort.Quantity)));
        Assert.All(inLeg.Lines!, line => Assert.Equal("DEPOTRAN", line.FromWarehouseCode));
        Assert.Contains("Part-accepted by Graniteside Controller", inLeg.Comments);
    }

    [Fact]
    public async Task Another_depot_cannot_see_or_accept_the_truck()
    {
        var sent = await Deliver(Douglas, "KEFGRC", "ACE1234", "Waiti", null, ("DISPYOG", "YOG500", 40m));
        var id = sent.Value.Transfer.Summary.Id;

        var result = await Decide(Machipisa, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 40m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.NotFound", result.FirstError.Code);
        Assert.Empty((await List(Machipisa, GetFactoryTransfersQuery.All)).Items);
    }

    [Fact]
    public async Task Accepting_for_any_department_does_not_reach_a_depot()
    {
        var sent = await Deliver(Douglas, "KEFGRC", "ACE1234", "Waiti", null, ("DISPYOG", "YOG500", 40m));
        var id = sent.Value.Transfer.Summary.Id;

        var listed = Assert.Single((await List(Millicent, GetFactoryTransfersQuery.All)).Items);
        Assert.False(listed.CanDecide);
        Assert.Equal(0, (await List(Millicent, GetFactoryTransfersQuery.Awaiting)).AwaitingMeCount);

        var result = await Decide(Millicent, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 40m));
        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.DestinationNotAssigned", result.FirstError.Code);
    }

    [Fact]
    public async Task Loading_a_truck_for_a_depot_order_closes_the_order()
    {
        _sap.Orders[10241] = new InventoryTransferRequest { DocEntry = 10241, DocNum = 10241, ToWarehouse = "KEFGRC", DocumentStatus = "bost_Open" };

        var result = await Deliver(Douglas, "KEFGRC", "ACE1234", "Waiti", 10241, ("DISPYOG", "YOG500", 40m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(10241, result.Value.Transfer.Summary.SourceRequestDocNum);
        Assert.Equal([10241], _sap.ClosedOrders);
        Assert.Equal([10241], _generatedFor);

        var again = await Deliver(Douglas, "KEFGRC", "ACE5678", "Kelvin", 10241, ("DISPYOG", "YOG500", 5m));
        Assert.True(again.IsError);
        Assert.Equal("FactoryTransfer.DepotOrderNotOpen", again.FirstError.Code);
    }

    [Fact]
    public async Task A_truck_cannot_fulfil_another_depots_order()
    {
        _sap.Orders[10239] = new InventoryTransferRequest { DocEntry = 10239, DocNum = 10239, ToWarehouse = "CORMACH", DocumentStatus = "bost_Open" };

        var result = await Deliver(Douglas, "KEFGRC", "ACE1234", "Waiti", 10239, ("DISPYOG", "YOG500", 40m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.DepotOrderForAnotherDepot", result.FirstError.Code);
        Assert.Empty(_sap.Created);
    }

    [Fact]
    public async Task A_depot_returns_stock_to_dispatch_and_distribution_accepts_it()
    {
        var sent = await Send(Machipisa, "CORMACH2", "DISPYOG", "Short supply", ("YOG500", 6m));
        Assert.False(sent.IsError, sent.IsError ? sent.FirstError.Description : null);
        var id = sent.Value.Transfer.Summary.Id;
        Assert.Equal(FactoryTransferDirections.DepotReturn, sent.Value.Transfer.Summary.Direction);
        Assert.Equal("Machipisa", sent.Value.Transfer.Summary.DepartmentName);
        Assert.Equal(("CORMACH2", "DEPOTRAN"), (_sap.Created[0].FromWarehouse, _sap.Created[0].ToWarehouse));

        var result = await Decide(Douglas, id, DecideFactoryTransferCommand.Accept, null, Accepted(id, 6m));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("DISPYOG", Assert.Single(_sap.Created[1].Lines!).ToWarehouseCode);
    }

    [Fact]
    public async Task A_depot_cannot_send_to_a_production_warehouse()
    {
        var result = await Send(Machipisa, "CORMACH", "PROYG", "Wrong door", ("YOG500", 6m));

        Assert.True(result.IsError);
        Assert.Equal("FactoryTransfer.InvalidDestination", result.FirstError.Code);
    }

    [Fact]
    public async Task Distribution_is_offered_its_dispatch_warehouses_and_every_depot()
    {
        var options = await new GetFactoryTransferOptionsHandler(_context, Users())
            .Handle(new GetFactoryTransferOptionsQuery(Douglas), default);

        Assert.False(options.IsError);
        Assert.NotNull(options.Value.DepotDelivery);
        Assert.Equal(["DISPYOG", "DISPMILK", "DISPICE", "DISPCH"], options.Value.DepotDelivery!.DispatchWarehouses);
        Assert.Equal(["KEFGRC", "CORMACH", "KEFBYC"], options.Value.DepotDelivery.Depots.Select(depot => depot.Code));
        Assert.Empty(options.Value.Routes);

        var depot = await new GetFactoryTransferOptionsHandler(_context, Users())
            .Handle(new GetFactoryTransferOptionsQuery(Machipisa), default);
        Assert.Null(depot.Value.DepotDelivery);
        Assert.Equal(["CORMACH", "CORMACH2"], depot.Value.Routes.Select(route => route.SourceWarehouseCode));
        Assert.All(depot.Value.Routes, route => Assert.Equal(FactoryTransferDirections.DepotReturn, route.Direction));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static User NewUser(Guid id, string username, string role, string first, string last, List<string> warehouses, List<string>? permissions = null)
    {
        var user = new User
        {
            Id = id,
            Username = username,
            PasswordHash = "x",
            Role = role,
            FirstName = first,
            LastName = last,
            IsActive = true,
            Permissions = permissions is null ? null : JsonSerializer.Serialize(permissions)
        };
        user.SetWarehouseCodes(warehouses);
        return user;
    }

    private IUserManagementService Users()
        => new UserManagementService(_context, new MemoryCache(new MemoryCacheOptions()), NullLogger<UserManagementService>.Instance);

    private IFactoryTransferLegPoster Poster()
        => new FactoryTransferLegPoster(
            _context,
            _sap.AsClient(),
            StubProxy.For<IStockValidationService>((method, _) => method.Name switch
            {
                nameof(IStockValidationService.ValidateInventoryTransferStockAsync) => (object)Task.FromResult(StockValidationResult.Success()),
                _ => throw new InvalidOperationException($"Unexpected stock-validation call: {method.Name}")
            }),
            new IdempotencyRequestStore(new SingleContextScopeFactory(_options), Options.Create(new SecuritySettings())),
            new FactoryTransferFollowUps(
                _context,
                _sap.AsClient(),
                StubProxy.For<IInventoryTransferApprovalService>((method, args) => method.Name switch
                {
                    nameof(IInventoryTransferApprovalService.MarkGeneratedAsync) => RecordGenerated(args!),
                    _ => throw new InvalidOperationException($"Unexpected approval call: {method.Name}")
                }),
                StubProxy.For<INotificationService>((method, args) => method.Name switch
                {
                    nameof(INotificationService.CreateNotificationAsync) => RecordNotification((CreateNotificationRequest)args![0]!),
                    _ => throw new InvalidOperationException($"Unexpected notification call: {method.Name}")
                }),
                NullLogger<FactoryTransferFollowUps>.Instance),
            NullLogger<FactoryTransferLegPoster>.Instance);

    private readonly List<CreateNotificationRequest> _notifications = [];
    private readonly List<int> _generatedFor = [];

    private Task<NotificationDto> RecordNotification(CreateNotificationRequest request)
    {
        _notifications.Add(request);
        return Task.FromResult(new NotificationDto());
    }

    private Task RecordGenerated(object?[] args)
    {
        _generatedFor.Add((int)args[0]!);
        return Task.CompletedTask;
    }

    private static IAuditService Audit() => StubProxy.For<IAuditService>((_, _) => Task.CompletedTask);

    private static IOptions<SAPSettings> Sap() => Options.Create(new SAPSettings { Enabled = true });

    private Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>> Send(
        Guid user, string source, string? destination, string reason, params (string Code, decimal Quantity)[] lines)
        => Send(user, source, destination, reason, $"form-{++_requests}", lines);

    private Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>> Send(
        Guid user, string source, string? destination, string reason, string clientRequestId, params (string Code, decimal Quantity)[] lines)
    {
        _context.ChangeTracker.Clear();
        return new SendFactoryTransferHandler(_context, Users(), Poster(), _sap.AsClient(), Audit(), Sap(), NullLogger<SendFactoryTransferHandler>.Instance)
            .Handle(new SendFactoryTransferCommand(
                clientRequestId,
                source,
                destination,
                reason,
                lines.Select(line => new SendFactoryTransferLineDto { ItemCode = line.Code, Quantity = line.Quantity }).ToList(),
                user), default);
    }

    /// <summary>A truck for a depot: each line names the dispatch warehouse it is loaded from.</summary>
    private Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>> Deliver(
        Guid user, string depot, string? plate, string? driver, int? order, params (string From, string Code, decimal Quantity)[] lines)
    {
        _context.ChangeTracker.Clear();
        return new SendFactoryTransferHandler(_context, Users(), Poster(), _sap.AsClient(), Audit(), Sap(), NullLogger<SendFactoryTransferHandler>.Instance)
            .Handle(new SendFactoryTransferCommand(
                $"truck-{++_requests}",
                lines[0].From,
                depot,
                "Graniteside order",
                lines.Select(line => new SendFactoryTransferLineDto { FromWarehouseCode = line.From, ItemCode = line.Code, Quantity = line.Quantity }).ToList(),
                user,
                plate,
                driver,
                order), default);
    }

    private async Task<int> GivenReleaseAsync(params (string Code, decimal Quantity)[] lines)
    {
        var sent = await Send(Eunice, "PROYG", null, "End of shift", lines);
        Assert.False(sent.IsError, sent.IsError ? sent.FirstError.Description : null);
        return sent.Value.Transfer.Summary.Id;
    }

    private List<DecideFactoryTransferLineDto> Accepted(int id, params decimal[] quantities)
    {
        var lineIds = _context.FactoryTransferLines.AsNoTracking()
            .Where(line => line.TransferId == id)
            .OrderBy(line => line.LineNum)
            .Select(line => line.Id)
            .ToList();
        return lineIds.Zip(quantities, (lineId, quantity) => new DecideFactoryTransferLineDto { LineId = lineId, AcceptedQuantity = quantity }).ToList();
    }

    private Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>> Decide(
        Guid user, int id, string decision, string? reason, List<DecideFactoryTransferLineDto> lines)
    {
        _context.ChangeTracker.Clear();
        return new DecideFactoryTransferHandler(_context, Users(), Poster(), Audit(), Sap(), NullLogger<DecideFactoryTransferHandler>.Instance)
            .Handle(new DecideFactoryTransferCommand(id, decision, reason, lines, user), default);
    }

    private Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>> Recall(Guid user, int id, string reason)
    {
        _context.ChangeTracker.Clear();
        return new RecallFactoryTransferHandler(_context, Users(), Poster(), Audit(), Sap(), NullLogger<RecallFactoryTransferHandler>.Instance)
            .Handle(new RecallFactoryTransferCommand(id, reason, user), default);
    }

    private Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>> Retry(Guid user, int id)
    {
        _context.ChangeTracker.Clear();
        return new RetryFactoryTransferHandler(_context, Users(), Poster(), Audit(), Sap(), NullLogger<RetryFactoryTransferHandler>.Instance)
            .Handle(new RetryFactoryTransferCommand(id, user), default);
    }

    private Task<ErrorOr.ErrorOr<FactoryTransferActionResultDto>> Cancel(Guid user, int id)
    {
        _context.ChangeTracker.Clear();
        return new CancelFactoryTransferHandler(_context, Users(), _sap.AsClient(), Poster(), Audit(), NullLogger<CancelFactoryTransferHandler>.Instance)
            .Handle(new CancelFactoryTransferCommand(id, user), default);
    }

    private async Task<FactoryTransferListResponseDto> List(Guid user, string view)
    {
        var result = await new GetFactoryTransfersHandler(_context, Users())
            .Handle(new GetFactoryTransfersQuery(user, view, null, 1, 25), default);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<FactoryTransferEntity> ReloadAsync(int id)
    {
        await using var fresh = new ApplicationDbContext(_options);
        return await fresh.FactoryTransfers.AsNoTracking().Include(transfer => transfer.Lines).SingleAsync(transfer => transfer.Id == id);
    }

    /// <summary>
    /// SAP as the poster sees it. Every line is put in one batch named after its item; a lost reply
    /// creates the document and then times out, as a real one does.
    /// </summary>
    private sealed class FakeSap
    {
        public const int FirstDocNum = 9001;

        public List<CreateInventoryTransferRequest> Created { get; } = [];
        private readonly Dictionary<string, InventoryTransfer> _byOrderNumber = new();
        public Dictionary<int, InventoryTransferRequest> Orders { get; } = new();
        public List<int> ClosedOrders { get; } = [];

        public Exception? RefuseNext { get; set; }
        public bool LoseNextReply { get; set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.CreateInventoryTransferAsync) =>
                    Create((CreateInventoryTransferRequest)args![0]!),
                nameof(ISAPServiceLayerClient.FindStockTransferByOrderNumberAsync) =>
                    Task.FromResult(_byOrderNumber.GetValueOrDefault((string)args![0]!)),
                nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync) =>
                    Task.FromResult(Orders.GetValueOrDefault((int)args![0]!)),
                nameof(ISAPServiceLayerClient.CloseInventoryTransferRequestAsync) => Close((int)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task Close(int docEntry)
        {
            ClosedOrders.Add(docEntry);
            if (Orders.TryGetValue(docEntry, out var order))
                order.DocumentStatus = "bost_Close";
            return Task.CompletedTask;
        }

        private Task<InventoryTransfer> Create(CreateInventoryTransferRequest request)
        {
            if (RefuseNext is { } refusal)
            {
                RefuseNext = null;
                throw refusal;
            }

            Created.Add(request);
            var document = new InventoryTransfer
            {
                DocEntry = 100 + Created.Count,
                DocNum = FirstDocNum + Created.Count - 1,
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse,
                Comments = request.Comments,
                StockTransferLines = request.Lines!.Select((line, index) => new InventoryTransferLine
                {
                    LineNum = index,
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    FromWarehouseCode = line.FromWarehouseCode,
                    WarehouseCode = line.ToWarehouseCode,
                    BatchNumbers = line.BatchNumbers is { Count: > 0 } chosen
                        ? chosen.Select(batch => new InventoryTransferLineBatch { BatchNumber = batch.BatchNumber, Quantity = batch.Quantity }).ToList()
                        : [new InventoryTransferLineBatch { BatchNumber = $"{line.ItemCode}-B1", Quantity = line.Quantity }]
                }).ToList()
            };
            _byOrderNumber[request.OrderNumber!] = document;

            if (LoseNextReply)
            {
                LoseNextReply = false;
                throw new TaskCanceledException("The SAP client timed out.");
            }

            return Task.FromResult(document);
        }
    }

    private sealed class SingleContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;
        public void Dispose() { }
    }
}
