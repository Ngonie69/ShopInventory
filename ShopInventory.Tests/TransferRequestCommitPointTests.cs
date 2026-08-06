using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.CreateTransferRequest;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the point in transfer request creation past which the caller no longer gets a vote.
/// </summary>
/// <remarks>
/// The approval row is what makes a transfer request exist for the application: the Service Layer
/// bypasses B1's own approval procedures, so the local engine is the only control over one. Creating
/// the document in SAP on the request token and then recording it on that same token put a client
/// disconnect between those two lines, and a disconnect there left SAP holding a transfer request
/// the approval engine had never heard of. Nothing would have found it again — EnsureRequestAsync is
/// idempotent, but only the create, edit and convert paths ever call it, and there is no
/// reconciliation job for approvals.
///
/// The guarantee is therefore not "the handler tolerates cancellation" but "past the commit point
/// the handler cannot see it", which is what these tests assert.
/// </remarks>
public sealed class TransferRequestCommitPointTests : IDisposable
{
    private static readonly Guid Requester = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>Cancelled from inside the SAP post, standing in for the client that hung up.</summary>
    private readonly CancellationTokenSource _caller = new();

    private bool _postAttempted;
    private bool _approvalRecorded;
    private CancellationToken _approvalToken = new CancellationTokenSource().Token;

    public TransferRequestCommitPointTests()
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
        _caller.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_caller_who_disconnects_mid_post_still_gets_the_approval_row_written()
    {
        await GivenRequesterAsync();

        // Disconnect at the worst possible moment: the document is in SAP, the approval row is not.
        var result = await CreateHandler().Handle(Command(), _caller.Token);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.True(
            _approvalRecorded,
            "The approval row must be written even though the caller disconnected — without it the "
            + "SAP document is invisible to the only approval engine that governs it.");
    }

    [Fact]
    public async Task The_approval_row_is_written_on_a_token_the_caller_cannot_cancel()
    {
        await GivenRequesterAsync();

        await CreateHandler().Handle(Command(), _caller.Token);

        // Passing the request token and merely hoping the window stays narrow is the bug. The
        // guarantee has to be structural: what reaches the approval service cannot be cancelled.
        Assert.False(
            _approvalToken.CanBeCanceled,
            "The commit path must run on CancellationToken.None, not on a token linked to the request.");
    }

    [Fact]
    public async Task A_caller_who_gives_up_before_the_commit_point_creates_nothing()
    {
        await GivenRequesterAsync();
        await _caller.CancelAsync();

        // The other half of the contract: cancellation is honoured right up to the commit point, so
        // an abandoned request does not post a document nobody is waiting to keep. It surfaces as a
        // throw rather than an error result, which is what RequestCanceledExceptionHandler turns
        // into a 499.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().Handle(Command(), _caller.Token));

        Assert.False(_postAttempted);
        Assert.False(_approvalRecorded);
    }

    private async Task GivenRequesterAsync()
    {
        _context.Users.Add(new User
        {
            Id = Requester,
            Username = "tanaka",
            FirstName = "Tanaka",
            LastName = "Moyo",
            Email = "tanaka@example.com",
            PasswordHash = "x",
            Role = "Manager",
            IsActive = true
        });

        await _context.SaveChangesAsync();
    }

    private static CreateTransferRequestCommand Command() =>
        new(
            new CreateTransferRequestDto
            {
                FromWarehouse = "KEFSHOP",
                ToWarehouse = "KEFDEPOT",
                Lines =
                [
                    new CreateTransferRequestLineDto
                    {
                        ItemCode = "YOG143",
                        Quantity = 5m,
                        UoMCode = "EA",
                        FromWarehouseCode = "KEFSHOP",
                        ToWarehouseCode = "KEFDEPOT"
                    }
                ]
            },
            Requester);

    private CreateTransferRequestHandler CreateHandler() =>
        new(
            _context,
            BuildSapClient(),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            BuildApprovalService(),
            StubProxy.For<INotificationService>((_, _) =>
                Task.FromResult(new NotificationDto())),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<CreateTransferRequestHandler>.Instance);

    private ISAPServiceLayerClient BuildSapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetWarehousesAsync) => Task.FromResult(new List<WarehouseDto>
            {
                new() { WarehouseCode = "KEFSHOP" },
                new() { WarehouseCode = "KEFDEPOT" }
            }),
            nameof(ISAPServiceLayerClient.CreateInventoryTransferRequestAsync) => PostAndDisconnect(),
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private Task<InventoryTransferRequest> PostAndDisconnect()
    {
        _postAttempted = true;

        // SAP has the document. This is the instant the old code lost it.
        _caller.Cancel();

        return Task.FromResult(new InventoryTransferRequest { DocEntry = 4210, DocNum = 8815 });
    }

    private IInventoryTransferApprovalService BuildApprovalService() =>
        StubProxy.For<IInventoryTransferApprovalService>((method, args) =>
        {
            if (method.Name != nameof(IInventoryTransferApprovalService.EnsureRequestAsync))
            {
                throw new InvalidOperationException(
                    $"IInventoryTransferApprovalService.{method.Name} was not expected on this path.");
            }

            var token = (CancellationToken)args![2]!;
            _approvalToken = token;

            // The real service reaches EF on this token, so a cancelled one throws before the row
            // is written. A stub that ignored it would record an approval the production code
            // never got to make, and this whole file would pass against the bug it was written for.
            token.ThrowIfCancellationRequested();

            _approvalRecorded = true;
            return Task.FromResult(new ApprovalRequestEntity());
        });
}
