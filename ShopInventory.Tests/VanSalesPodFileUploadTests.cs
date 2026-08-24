using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Data;
using ShopInventory.Features.Invoices.Commands.UploadPod;
using ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPodFile;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The van sales half of the file-based POD upload — the same one the drivers' app makes, reachable
/// by a van rep.
/// </summary>
/// <remarks>
/// It exists because <c>InvoiceController.UploadPod</c> is gated <c>[Authorize(Roles = "…,SalesRep")]</c>
/// and a van rep's role is <c>Sales</c>, a different role, so every van upload there is a 403. What
/// these pin is that the mirroring is faithful: the caller's page, name, media type and idempotency
/// key reach <see cref="UploadPodCommand"/> untouched, and the additional-page flag survives the hop.
/// A handset that mints a stable key per photo and has it dropped on the way through would have its
/// retries stored as new pages.
/// </remarks>
public sealed class VanSalesPodFileUploadTests : IDisposable
{
    private const int Order = 758051;
    private static readonly Guid Rep = Guid.Parse("9d4b7e21-6c58-4a33-b0f7-1e2a3b4c5d6e");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<UploadPodCommand> _sent = [];

    public VanSalesPodFileUploadTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new ShopInventory.Models.User
        {
            Id = Rep,
            Username = "van-rep",
            PasswordHash = "not-a-real-hash",
            Role = "Sales",
            IsActive = true
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// A van rep's role is exactly the one the portal's own POD route refuses, so the mirrored route
    /// accepting it is the whole point of the route existing.
    /// </summary>
    [Fact]
    public async Task A_van_rep_may_file_a_page()
    {
        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(result.IsError);

        // No sales order and no matching invoice row, so the id the handset sent resolves to itself —
        // the same hop the JSON route makes, through the shared VanSalesPodTarget. SAP is asked about
        // that number next, which is what stops a wrong one being filed against.
        Assert.Equal(Order, Assert.Single(_sent).DocEntry);
    }

    /// <summary>
    /// The key the handset mints per photo is identical on every attempt, including one made by a
    /// background worker after the app was killed. Dropped here, a retry becomes a second page.
    /// </summary>
    [Fact]
    public async Task The_callers_idempotency_key_reaches_the_upload_untouched()
    {
        await Handler().Handle(Command(reference: "MOBILE-POD-758051-A1B2C3D4E5F6A7B8C9D0E1F2"), CancellationToken.None);

        Assert.Equal("MOBILE-POD-758051-A1B2C3D4E5F6A7B8C9D0E1F2", Assert.Single(_sent).ExternalReference);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_additional_page_flag_survives_the_hop(bool additional)
    {
        await Handler().Handle(Command(additional: additional), CancellationToken.None);

        Assert.Equal(additional, Assert.Single(_sent).IsAdditionalPage);
    }

    [Fact]
    public async Task The_page_keeps_its_name_and_media_type()
    {
        await Handler().Handle(Command(), CancellationToken.None);

        var page = Assert.Single(_sent);
        Assert.Equal("POD_758051_1.jpg", page.FileName);
        Assert.Equal("image/jpeg", page.ContentType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_page_with_no_document_to_file_against_is_refused(int order)
    {
        var result = await Handler().Handle(Command(order: order), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(_sent);
    }

    /// <summary>
    /// SAP has the last word. A document number this company database does not know is refused before
    /// anything is stored, rather than filed against whatever that number happens to be.
    /// </summary>
    [Fact]
    public async Task A_document_sap_does_not_know_is_refused()
    {
        var result = await Handler(invoiceInSap: false).Handle(Command(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused()
    {
        var rep = _context.Users.First();
        rep.IsActive = false;
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(_sent);
    }

    private static UploadVanSalesPodFileCommand Command(
        int order = Order,
        string? reference = "MOBILE-POD-758051-DEADBEEFDEADBEEFDEADBEEF",
        bool additional = false) =>
        new(
            order,
            new MemoryStream([1, 2, 3, 4]),
            $"POD_{Order}_1.jpg",
            "image/jpeg",
            "Proof of Delivery",
            reference,
            additional,
            Rep);

    private UploadVanSalesPodFileHandler Handler(bool invoiceInSap = true)
    {
        return new UploadVanSalesPodFileHandler(
            _context,
            StubProxy.For<IMediator>((method, args) =>
            {
                if (method.Name != nameof(IMediator.Send))
                    throw new InvalidOperationException($"Unexpected call to {method.Name}");

                _sent.Add((UploadPodCommand)args![0]!);

                return Task.FromResult<ErrorOr<DocumentAttachmentDto>>(new DocumentAttachmentDto
                {
                    Id = 19107,
                    EntityType = "Invoice",
                    EntityId = Order,
                    FileName = $"POD_{Order}_1.jpg"
                });
            }),
            StubProxy.For<ISAPServiceLayerClient>((method, _) =>
                method.Name == nameof(ISAPServiceLayerClient.GetInvoiceByDocEntryAsync)
                    ? Task.FromResult(invoiceInSap
                        ? new ShopInventory.Models.Invoice { DocEntry = Order, DocNum = 2232744 }
                        : null)
                    : throw new InvalidOperationException($"Unexpected call to {method.Name}")),
            NullLogger<UploadVanSalesPodFileHandler>.Instance);
    }
}
