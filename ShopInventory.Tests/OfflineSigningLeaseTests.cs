using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.FiscalisationConfiguration.Commands.AssignOfflineSigningLease;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetOfflineSigningLease;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscalLease;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Which handset may sign offline on a fiscal device, and — the part that matters — when that may be
/// moved to another one.
///
/// The fleet shares a single ZIMRA device, so its receipts are one hash-chained sequence. Handing offline
/// signing to a second van while the first still carries signed receipts does not divide the work, it
/// forks the chain: the new holder starts from a position that does not know about the receipts still on
/// the old handset and signs over numbers already spent. FDMS refuses the whole fiscal day when the file
/// is uploaded, long after the customers have gone. None of that is reproducible in the field without
/// causing it, so it is asserted here.
/// </summary>
public sealed class OfflineSigningLeaseTests : IDisposable
{
    private const int DeviceId = 35410;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public OfflineSigningLeaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);

        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Nominating_a_handset_records_it_as_the_holder()
    {
        var van = await SeedVanAsync("VAN003");

        var result = await AssignAsync(van.Id);

        Assert.False(result.IsError);
        Assert.Equal(van.Id, result.Value.HolderUserId);
        Assert.Contains("VAN003", result.Value.HolderLabel);
    }

    /// <summary>
    /// A fresh nomination knows nothing about the new holder's queue, so it cannot yet be handed on. The
    /// alternative — treating silence as an empty queue — is what makes a forked chain possible.
    /// </summary>
    [Fact]
    public async Task A_new_holder_has_not_reported_so_the_device_cannot_be_handed_on_yet()
    {
        var van = await SeedVanAsync("VAN003");

        var result = await AssignAsync(van.Id);

        Assert.Null(result.Value.HolderPendingSales);
        Assert.False(result.Value.CanHandOver);
    }

    [Fact]
    public async Task A_holder_still_carrying_receipts_blocks_the_handover()
    {
        var outgoing = await SeedVanAsync("VAN003");
        var incoming = await SeedVanAsync("VAN005");

        await AssignAsync(outgoing.Id);
        await ReportQueueAsync(pendingSales: 4);

        var result = await AssignAsync(incoming.Id);

        Assert.True(result.IsError);
        Assert.Contains("VAN003", result.FirstError.Description);
        Assert.Contains("4 signed receipts", result.FirstError.Description);
        Assert.Contains("refuses a whole fiscal day", result.FirstError.Description);
    }

    [Fact]
    public async Task A_holder_that_has_drained_its_queue_hands_over_freely()
    {
        var outgoing = await SeedVanAsync("VAN003");
        var incoming = await SeedVanAsync("VAN005");

        await AssignAsync(outgoing.Id);
        await ReportQueueAsync(pendingSales: 0);

        var result = await AssignAsync(incoming.Id);

        Assert.False(result.IsError);
        Assert.Equal(incoming.Id, result.Value.HolderUserId);
    }

    /// <summary>
    /// A handset that is lost, broken or stolen will never report an empty queue, and the fleet still has
    /// to trade. Forcing is the answer — it just cannot be the default.
    /// </summary>
    [Fact]
    public async Task Forcing_moves_a_device_away_from_a_handset_that_is_not_coming_back()
    {
        var outgoing = await SeedVanAsync("VAN003");
        var incoming = await SeedVanAsync("VAN005");

        await AssignAsync(outgoing.Id);
        await ReportQueueAsync(pendingSales: 4);

        var result = await AssignAsync(incoming.Id, force: true);

        Assert.False(result.IsError);
        Assert.Equal(incoming.Id, result.Value.HolderUserId);
        Assert.Null(result.Value.HolderPendingSales);
    }

    /// <summary>Re-confirming the same van must not throw away what it has told us about its queue.</summary>
    [Fact]
    public async Task Re_confirming_the_same_holder_keeps_its_reported_queue()
    {
        var van = await SeedVanAsync("VAN003");

        await AssignAsync(van.Id);
        await ReportQueueAsync(pendingSales: 0);

        var result = await AssignAsync(van.Id);

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.HolderPendingSales);
        Assert.True(result.Value.CanHandOver);
    }

    [Fact]
    public async Task Clearing_the_nomination_leaves_nobody_able_to_sign_offline()
    {
        var van = await SeedVanAsync("VAN003");

        await AssignAsync(van.Id);
        await ReportQueueAsync(pendingSales: 0);

        var result = await AssignAsync(holderUserId: null);

        Assert.False(result.IsError);
        Assert.Null(result.Value.HolderUserId);
    }

    /// <summary>
    /// Nominating a user whose handset signs as another device reads as permission and grants nothing —
    /// the lease it collects is for the device on its own record.
    /// </summary>
    [Fact]
    public async Task A_handset_registered_to_another_device_cannot_be_nominated()
    {
        var stranger = await SeedVanAsync("VAN009", deviceId: 99999);

        var result = await AssignAsync(stranger.Id);

        Assert.True(result.IsError);
        Assert.Contains("99999", result.FirstError.Description);
    }

    [Fact]
    public async Task A_handset_with_no_fiscal_device_cannot_be_nominated()
    {
        var stranger = await SeedVanAsync("VAN009", deviceId: null);

        var result = await AssignAsync(stranger.Id);

        Assert.True(result.IsError);
        Assert.Contains("not registered against any fiscal device", result.FirstError.Description);
    }

    /// <summary>With nobody nominated, no handset may sign offline — the safe default, not a fault.</summary>
    [Fact]
    public async Task An_unnominated_device_issues_no_lease()
    {
        var van = await SeedVanAsync("VAN003");

        var result = await RequestLeaseAsync(van.Id);

        Assert.True(result.IsError);
        Assert.Contains("No handset is nominated", result.FirstError.Description);
    }

    /// <summary>
    /// The refusal 11 of the 12 handsets get every day. It names the holder because the rep's next move is
    /// to call the office, and "no lease" alone would send them hunting for signal they already have.
    /// </summary>
    [Fact]
    public async Task A_handset_that_is_not_the_holder_is_refused_and_told_who_has_it()
    {
        var holder = await SeedVanAsync("VAN003");
        var other = await SeedVanAsync("VAN005");

        await AssignAsync(holder.Id);

        var result = await RequestLeaseAsync(other.Id);

        Assert.True(result.IsError);
        Assert.Contains("VAN003", result.FirstError.Description);
        Assert.Contains("only one handset can", result.FirstError.Description);
    }

    /// <summary>
    /// The holder gets past the gate, and its report of what it is still carrying is written down on the
    /// way through — which is the only moment the server ever hears it.
    /// </summary>
    [Fact]
    public async Task The_holder_passes_the_gate_and_its_queue_report_is_recorded()
    {
        var holder = await SeedVanAsync("VAN003");
        await AssignAsync(holder.Id);

        // Stops at the platform, which is past the gate and far enough to prove the point.
        var result = await RequestLeaseAsync(holder.Id, pendingSales: 3);

        Assert.True(result.IsError);
        Assert.Contains("fiscal configuration", result.FirstError.Description);

        var nomination = await _context.FiscalDeviceOfflineLeases.AsNoTracking()
            .FirstAsync(row => row.DeviceId == DeviceId);

        Assert.Equal(3, nomination.HolderPendingSales);
        Assert.NotNull(nomination.HolderLastSeenAtUtc);
        Assert.Null(nomination.ReleasedAtUtc);
        Assert.False(nomination.CanHandOver);
    }

    /// <summary>A handset too old to report its queue is recorded as unknown, and never as empty.</summary>
    [Fact]
    public async Task A_handset_that_reports_nothing_is_recorded_as_unknown()
    {
        var holder = await SeedVanAsync("VAN003");
        await AssignAsync(holder.Id);

        await RequestLeaseAsync(holder.Id, pendingSales: null);

        var nomination = await _context.FiscalDeviceOfflineLeases.AsNoTracking()
            .FirstAsync(row => row.DeviceId == DeviceId);

        Assert.Null(nomination.HolderPendingSales);
        Assert.False(nomination.CanHandOver);
    }

    /// <summary>
    /// The office's screen is built from this one call, so it has to find the fleet's device without
    /// being told which one it is.
    /// </summary>
    [Fact]
    public async Task The_overview_lists_each_device_the_fleet_is_registered_against()
    {
        await SeedVanAsync("VAN003");
        await SeedVanAsync("VAN005");
        await SeedVanAsync("VAN009", deviceId: 99999);

        var overview = await OverviewAsync();

        Assert.Equal(2, overview.Count);
        Assert.Equal(DeviceId, overview[0].Lease.DeviceId);
        Assert.Equal(99999, overview[1].Lease.DeviceId);
    }

    /// <summary>The shared-device case: one card, every van on it to choose from.</summary>
    [Fact]
    public async Task Every_handset_on_a_device_is_offered_as_a_candidate()
    {
        await SeedVanAsync("VAN005");
        await SeedVanAsync("VAN003");

        var overview = await OverviewAsync();

        var candidates = Assert.Single(overview).Candidates;

        Assert.Equal(2, candidates.Count);
        Assert.Collection(
            candidates,
            first => Assert.Contains("VAN003", first.Label),
            second => Assert.Contains("VAN005", second.Label));
    }

    [Fact]
    public async Task A_device_nobody_is_nominated_for_reads_as_unassigned_rather_than_missing()
    {
        await SeedVanAsync("VAN003");

        var lease = Assert.Single(await OverviewAsync()).Lease;

        Assert.Null(lease.HolderUserId);
        Assert.True(lease.CanHandOver);
    }

    [Fact]
    public async Task The_overview_carries_the_current_holder_and_its_reported_queue()
    {
        var van = await SeedVanAsync("VAN003");

        await AssignAsync(van.Id);
        await ReportQueueAsync(pendingSales: 2);

        var lease = Assert.Single(await OverviewAsync()).Lease;

        Assert.Equal(van.Id, lease.HolderUserId);
        Assert.Equal(2, lease.HolderPendingSales);
        Assert.False(lease.CanHandOver);
    }

    /// <summary>A handset that has been switched off is not a van the office can send anywhere.</summary>
    [Fact]
    public async Task Inactive_handsets_are_not_offered()
    {
        await SeedVanAsync("VAN003");
        await SeedVanAsync("VAN005", isActive: false);

        var candidates = Assert.Single(await OverviewAsync()).Candidates;

        Assert.Contains("VAN003", Assert.Single(candidates).Label);
    }

    [Fact]
    public async Task A_fleet_with_no_registered_handsets_has_nothing_to_show()
    {
        await SeedVanAsync("VAN003", deviceId: null);

        Assert.Empty(await OverviewAsync());
    }

    private async Task<List<ShopInventory.DTOs.FiscalDeviceOfflineLeaseSummaryDto>> OverviewAsync()
    {
        var handler = new GetOfflineSigningLeaseOverviewHandler(_context);
        var result = await handler.Handle(new GetOfflineSigningLeaseOverviewQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private Task<ErrorOr.ErrorOr<ShopInventory.DTOs.VanSalesFiscalLeaseDto>> RequestLeaseAsync(
        Guid userId,
        int? pendingSales = null)
    {
        var handler = new GetVanSalesFiscalLeaseHandler(
            _context,
            new NoDeviceConfig(),
            fiscalisationClient: null!,
            sapClient: null!,
            Options.Create(new FiscalisationSettings { Enabled = true }),
            NullLogger<GetVanSalesFiscalLeaseHandler>.Instance);

        return handler.Handle(
            new GetVanSalesFiscalLeaseQuery(userId, pendingSales), CancellationToken.None);
    }

    /// <summary>
    /// A platform that answers "no config for that device", which stops the handler just past the gate.
    /// The clients beyond it are never reached, so the test does not have to stand them up.
    /// </summary>
    private sealed class NoDeviceConfig : IFiscalDeviceConfigCache
    {
        public Task<FiscalConfigApiResponse?> TryGetAsync(
            int deviceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FiscalConfigApiResponse?>(null);
    }

    private Task<ErrorOr.ErrorOr<ShopInventory.DTOs.FiscalDeviceOfflineLeaseDto>> AssignAsync(
        Guid? holderUserId,
        bool force = false)
    {
        var handler = new AssignOfflineSigningLeaseHandler(
            _context, NullLogger<AssignOfflineSigningLeaseHandler>.Instance);

        return handler.Handle(
            new AssignOfflineSigningLeaseCommand(DeviceId, holderUserId, force, Guid.NewGuid(), "Office"),
            CancellationToken.None);
    }

    /// <summary>Stands in for the holder's handset checking in with how much it is still carrying.</summary>
    private async Task ReportQueueAsync(int? pendingSales)
    {
        var nomination = await _context.FiscalDeviceOfflineLeases.FirstAsync(row => row.DeviceId == DeviceId);
        var now = DateTime.UtcNow;

        nomination.HolderLastSeenAtUtc = now;
        nomination.HolderPendingSales = pendingSales;
        nomination.ReleasedAtUtc = pendingSales == 0 ? now : null;

        await _context.SaveChangesAsync();
    }

    private async Task<User> SeedVanAsync(string warehouse, int? deviceId = DeviceId, bool isActive = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"{warehouse}-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.SalesRep,
            FirstName = "Test",
            LastName = "Sales",
            IsActive = isActive,
            FiscalDeviceId = deviceId
        };

        user.AssignedWarehouseCode = warehouse;

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }
}
