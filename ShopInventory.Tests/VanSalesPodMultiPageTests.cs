using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Commands.UploadPod;
using ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPod;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that a van's multi-page delivery note actually arrives, page for page.
/// </summary>
/// <remarks>
/// The handset sends a whole note in one request and this handler loops the images, sending each
/// through <see cref="UploadPodCommand"/>. That command drops any upload landing within its
/// double-submit window of the same uploader's last one on the same invoice — and pages of one
/// request arrive milliseconds apart, so every page after the first was read as the first arriving
/// again and silently discarded. Only page one was ever stored.
/// <para>
/// Nothing surfaced it. Each iteration returned the attachment the guard had reused, so no error was
/// raised, and the reply counts the images it was <i>sent</i> — a rep photographing three pages of a
/// signed note was told three had landed while the office held one. The whole point of a POD is
/// being able to produce it later, so a page that is missing is not noticed until somebody needs it.
/// </para>
/// </remarks>
public sealed class VanSalesPodMultiPageTests : IDisposable
{
    /// <summary>No sales order and no invoice row, so the handler resolves the id to itself.</summary>
    private const int Order = 758051;

    private static readonly Guid Rep = Guid.Parse("6f1d2c34-5a67-48b9-9c01-2d3e4f5a6b7c");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>Every page this handler handed on, in the order it handed them on.</summary>
    private readonly List<UploadPodCommand> _sent = [];

    public VanSalesPodMultiPageTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new ShopInventory.Models.User
        {
            Id = Rep,
            Username = "vansales-rep",
            PasswordHash = "not-a-real-hash",
            Role = "SalesRep",
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
    /// The bug itself. Three pages go out; the second and third have to say they are further pages,
    /// or the window swallows them.
    /// </summary>
    [Fact]
    public async Task Every_page_after_the_first_says_it_is_a_further_page()
    {
        var result = await Handler().Handle(Command(pages: 3), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(3, _sent.Count);
        Assert.False(_sent[0].IsAdditionalPage);
        Assert.True(_sent[1].IsAdditionalPage);
        Assert.True(_sent[2].IsAdditionalPage);
    }

    /// <summary>
    /// And the first page must not, because that is the guard doing its real job — a rep double
    /// tapping Send. Flagging every page would take the double-submit protection away entirely.
    /// </summary>
    [Fact]
    public async Task The_first_page_is_still_left_to_the_double_submit_window()
    {
        var result = await Handler().Handle(Command(pages: 1), CancellationToken.None);

        Assert.False(result.IsError);
        var only = Assert.Single(_sent);
        Assert.False(only.IsAdditionalPage);
    }

    /// <summary>
    /// What catches a genuine re-send instead: the reference is a hash of the page's own bytes, so
    /// distinct pages are distinct submissions and the same batch posted twice is recognised page
    /// for page. This is the guard the first page's exemption leans on.
    /// </summary>
    [Fact]
    public async Task Each_page_is_referenced_by_its_own_bytes()
    {
        await Handler().Handle(Command(pages: 3), CancellationToken.None);

        var references = _sent.Select(page => page.ExternalReference).ToList();

        Assert.Equal(3, references.Distinct(StringComparer.Ordinal).Count());
        Assert.All(references, reference =>
            Assert.StartsWith($"MOBILE-POD-{Order}-", reference, StringComparison.Ordinal));
    }

    /// <summary>
    /// The reply has always counted the images it was sent. That only became a true statement about
    /// what the office holds once every page started arriving.
    /// </summary>
    [Fact]
    public async Task The_reply_counts_pages_that_were_all_actually_handed_on()
    {
        var result = await Handler().Handle(Command(pages: 3), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("3", result.Value, StringComparison.Ordinal);
        Assert.Equal(3, _sent.Count);
    }

    /// <summary>Each page carries different bytes, as separate photographs of separate pages do.</summary>
    private static UploadVanSalesPodCommand Command(int pages) =>
        new(
            new VanSalesPodUploadRequest
            {
                Order = Order,
                Images = [.. Enumerable.Range(1, pages).Select(page => new VanSalesPodUploadImageDto
                {
                    Image = "data:image/jpeg;base64," + Convert.ToBase64String([(byte)page, 2, 3, 4])
                })]
            },
            Rep);

    private UploadVanSalesPodHandler Handler() =>
        new(
            _context,
            StubProxy.For<IMediator>((method, args) =>
            {
                if (method.Name != nameof(IMediator.Send))
                    throw new InvalidOperationException($"Unexpected call to {method.Name}");

                _sent.Add((UploadPodCommand)args![0]!);

                return Task.FromResult<ErrorOr<DocumentAttachmentDto>>(new DocumentAttachmentDto
                {
                    Id = 19100 + _sent.Count,
                    EntityType = "Invoice",
                    EntityId = Order,
                    FileName = $"POD_mobile-pod-{Order}-{_sent.Count}.jpg"
                });
            }),
            StubProxy.For<ISAPServiceLayerClient>((method, _) =>
                method.Name == nameof(ISAPServiceLayerClient.GetInvoiceByDocEntryAsync)
                    ? Task.FromResult<ShopInventory.Models.Invoice?>(new ShopInventory.Models.Invoice
                    {
                        DocEntry = Order,
                        DocNum = 2232744,
                        CardCode = "C0001",
                        CardName = "MAI RUFARO TUCKSHOP"
                    })
                    : throw new InvalidOperationException($"Unexpected call to {method.Name}")));
}
