using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Mobile;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Commands.UploadPod;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPodFile;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesPodDeliveries;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The van's proof-of-delivery list: the invoices for the shops on the drivers' shop list.
/// </summary>
/// <remarks>
/// What these pin is whose list it is. A van rep is <c>Sales</c> or <c>ADR</c>, never <c>Driver</c>, so
/// a report scoped by the caller's own role would hand them every invoice in the company. The scope has
/// to be the drivers' list, stated explicitly, whoever is asking — and a list with no shops on it has to
/// come back empty rather than unscoped.
/// </remarks>
public sealed class VanSalesPodDeliveriesTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("4f1c2a7e-9b3d-4e61-8a2f-6c5d7e8f9a01");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<GetPodUploadStatusQuery> _asked = [];

    public VanSalesPodDeliveriesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        AddUser(Rep, "van-rep", ApplicationRoles.Sales);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_van_rep_is_shown_the_drivers_shops_not_their_own_scope()
    {
        AddUser(Guid.NewGuid(), "driver-one", ApplicationRoles.Driver, "CUS-0388", "CUS-0412");

        var result = await Handler().Handle(Query(), CancellationToken.None);

        Assert.False(result.IsError);
        var asked = Assert.Single(_asked);
        Assert.Null(asked.UserId);
        Assert.Equal(["CUS-0388", "CUS-0412"], asked.CustomerCodeScope!.OrderBy(code => code));
        Assert.Equal(2, result.Value.ShopCount);
    }

    [Fact]
    public async Task No_shops_on_the_list_is_an_empty_answer_and_asks_nothing_of_the_report()
    {
        AddUser(Guid.NewGuid(), "driver-without-shops", ApplicationRoles.Driver);

        var result = await Handler().Handle(Query(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.ShopCount);
        Assert.Empty(result.Value.Invoices);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused()
    {
        AddUser(Guid.NewGuid(), "driver-one", ApplicationRoles.Driver, "CUS-0388");
        var rep = _context.Users.Single(user => user.Id == Rep);
        rep.IsActive = false;
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var result = await Handler().Handle(Query(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Credited_invoices_stay_on_the_list_flagged_and_newest_come_first()
    {
        var dto = GetVanSalesPodDeliveriesHandler.ToDeliveries(
            new PodUploadStatusReportDto
            {
                FromDate = "2026-09-14",
                ToDate = "2026-09-17",
                Items =
                [
                    new PodUploadStatusItemDto { DocEntry = 1, DocNum = 104221, DocDate = "2026-09-16" },
                    new PodUploadStatusItemDto
                    {
                        DocEntry = 2,
                        DocNum = 104240,
                        DocDate = "2026-09-17",
                        HasPod = true,
                        PodCount = 2,
                        PodUploadedByUsers = [new PodUploadUserSummaryDto { Username = "driver-one", FileCount = 2 }]
                    }
                ],
                FullyCreditedItems =
                [
                    new PodUploadStatusItemDto
                    {
                        DocEntry = 3,
                        DocNum = 104169,
                        DocDate = "2026-09-14",
                        IsFullyCredited = true,
                        CreditNoteNumber = "2031"
                    }
                ]
            },
            shopCount: 5);

        Assert.Equal([104240, 104221, 104169], dto.Invoices.Select(invoice => invoice.DocNum));
        Assert.True(dto.Invoices[2].IsFullyCredited);
        Assert.Equal("2031", dto.Invoices[2].CreditNoteNumber);
        Assert.Equal("driver-one", Assert.Single(dto.Invoices[0].Uploaders).Username);
        Assert.Equal(5, dto.ShopCount);
    }

    [Fact]
    public void The_range_a_handset_may_ask_for_is_bounded()
    {
        var validator = new GetVanSalesPodDeliveriesValidator();
        var from = new DateTime(2026, 9, 1);

        Assert.True(validator.Validate(new GetVanSalesPodDeliveriesQuery(Rep, from, from.AddDays(31))).IsValid);
        Assert.False(validator.Validate(new GetVanSalesPodDeliveriesQuery(Rep, from, from.AddDays(32))).IsValid);
        Assert.False(validator.Validate(new GetVanSalesPodDeliveriesQuery(Rep, from, from.AddDays(-1))).IsValid);
    }

    // ---- the list itself ------------------------------------------------------------------------

    [Fact]
    public async Task A_drivers_copy_of_the_list_is_preferred_over_an_operators()
    {
        AddUser(Guid.NewGuid(), "aaa-operator", ApplicationRoles.PodOperator, "CUS-OPERATOR");
        AddUser(Guid.NewGuid(), "zzz-driver", ApplicationRoles.Driver, "CUS-DRIVER");

        var list = await DriverShopList.FindAsync(_context, excludingUserId: null, CancellationToken.None);

        Assert.Equal("zzz-driver", list!.Username);
        Assert.Equal(["CUS-DRIVER"], list.CustomerCodes);
    }

    [Fact]
    public async Task Nobody_elses_role_carries_the_list()
    {
        AddUser(Guid.NewGuid(), "merchandiser", ApplicationRoles.Merchandiser, "CUS-0388");

        Assert.Null(await DriverShopList.FindAsync(_context, excludingUserId: null, CancellationToken.None));
    }

    // ---- the report, scoped explicitly ----------------------------------------------------------

    /// <summary>
    /// An empty scope is a list with no shops on it. Treating it as "no scope" would be the one mistake
    /// here that shows a van every invoice the company has raised, so it must not reach SAP at all.
    /// </summary>
    [Fact]
    public async Task An_empty_scope_is_an_empty_report_not_an_unscoped_one()
    {
        var handler = new GetPodUploadStatusHandler(
            StubProxy.Unused<ISAPServiceLayerClient>(),
            StubProxy.Unused<IDocumentService>(),
            _context,
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(new CreditNoteSyncSettings()),
            StubProxy.Unused<IPodReportCacheStore>(),
            new PodReportWarmSet(),
            NullLogger<GetPodUploadStatusHandler>.Instance);

        var result = await handler.Handle(
            new GetPodUploadStatusQuery(
                new DateTime(2026, 9, 1),
                new DateTime(2026, 9, 14),
                UserId: null,
                CustomerCodeScope: ["  ", ""]),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Empty(result.Value.Items);
        Assert.Empty(result.Value.FullyCreditedItems);
    }

    // ---- filing against the listed invoice --------------------------------------------------------

    /// <summary>
    /// The list hands the handset SAP document entries. Sent through the id-guessing route, one that
    /// equals a platform invoice id is filed against that invoice's document instead; the flag is what
    /// stops it.
    /// </summary>
    [Theory]
    [InlineData(true, 758051)]
    [InlineData(false, 9001)]
    public async Task A_document_entry_is_filed_against_exactly_that_invoice(bool isDocEntry, int expected)
    {
        _context.Invoices.Add(new InvoiceEntity { Id = 758051, SAPDocEntry = 9001, CardCode = "CUS-0001" });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var sent = new List<UploadPodCommand>();
        var handler = new UploadVanSalesPodFileHandler(
            _context,
            StubProxy.For<IMediator>((_, args) =>
            {
                sent.Add((UploadPodCommand)args![0]!);
                return Task.FromResult<ErrorOr<DocumentAttachmentDto>>(new DocumentAttachmentDto());
            }),
            StubProxy.For<ISAPServiceLayerClient>((method, args) =>
                method.Name == nameof(ISAPServiceLayerClient.GetInvoiceByDocEntryAsync)
                    ? Task.FromResult<Invoice?>(new Invoice { DocEntry = (int)args![0]! })
                    : throw new InvalidOperationException($"Unexpected call to {method.Name}")),
            NullLogger<UploadVanSalesPodFileHandler>.Instance);

        var result = await handler.Handle(
            new UploadVanSalesPodFileCommand(
                758051,
                new MemoryStream([1, 2, 3]),
                "POD_758051_1.jpg",
                "image/jpeg",
                "Proof of Delivery",
                "MOBILE-POD-758051-KEY",
                false,
                Rep,
                OrderIsSapDocEntry: isDocEntry),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(expected, Assert.Single(sent).DocEntry);
    }

    private GetVanSalesPodDeliveriesQuery Query() =>
        new(Rep, new DateTime(2026, 9, 3), new DateTime(2026, 9, 17));

    private GetVanSalesPodDeliveriesHandler Handler() =>
        new(
            _context,
            StubProxy.For<IMediator>((method, args) =>
            {
                if (method.Name != nameof(IMediator.Send))
                    throw new InvalidOperationException($"Unexpected call to {method.Name}");

                var query = (GetPodUploadStatusQuery)args![0]!;
                _asked.Add(query);

                return Task.FromResult<ErrorOr<PodUploadStatusReportDto>>(new PodUploadStatusReportDto
                {
                    FromDate = query.FromDate.ToString("yyyy-MM-dd"),
                    ToDate = query.ToDate.ToString("yyyy-MM-dd")
                });
            }),
            NullLogger<GetVanSalesPodDeliveriesHandler>.Instance);

    private void AddUser(Guid id, string username, string role, params string[] customerCodes)
    {
        var user = new User
        {
            Id = id,
            Username = username,
            PasswordHash = "not-a-real-hash",
            Role = role,
            IsActive = true
        };
        user.SetCustomerCodes([.. customerCodes]);

        _context.Users.Add(user);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }
}
