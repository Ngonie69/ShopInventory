using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Auth;
using ShopInventory.Common.Pods;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Drivers out of the four Cheeseman distribution centres give the truck's fleet number with every
/// POD. The app makes it mandatory; the server says who is asked and keeps what they typed.
/// </summary>
public sealed class PodFleetNumberTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly string _uploadPath;

    public PodFleetNumberTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _uploadPath = Path.Combine(Path.GetTempPath(), $"pod-fleet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_uploadPath);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_uploadPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    // Each section as it is stored, and as the other spellings it arrives under.
    [InlineData("Cheeseman")]
    [InlineData("Cheeseman DC Harare")]
    [InlineData("Cheeseman DC Richwell")]
    [InlineData("Cheeseman DC Vic Falls")]
    [InlineData("Bulawayo")]
    [InlineData("Cheeseman DC Byo")]
    [InlineData("cheeseman dc vic falls")]
    public void A_driver_out_of_a_cheeseman_dc_is_asked(string section)
    {
        Assert.True(PodFleetNumber.IsRequiredFor("Driver", section));
    }

    [Theory]
    [InlineData("Factory")]
    [InlineData("Factory - Dispatch")]
    [InlineData("Graniteside")]
    [InlineData("Machipisa")]
    [InlineData("Kefalos Byo")]
    [InlineData("")]
    [InlineData(null)]
    public void A_driver_anywhere_else_is_not_asked(string? section)
    {
        Assert.False(PodFleetNumber.IsRequiredFor("Driver", section));
    }

    [Theory]
    [InlineData("Merchandiser")]
    [InlineData("PodOperator")]
    [InlineData("Operator")]
    [InlineData("Admin")]
    public void Only_drivers_are_asked_even_inside_a_cheeseman_dc(string role)
    {
        Assert.False(PodFleetNumber.IsRequiredFor(role, "Cheeseman DC Richwell"));
    }

    [Theory]
    [InlineData(" ch  12 ", "CH 12")]
    [InlineData("abc123", "ABC123")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_fleet_number_is_stored_one_way_however_it_was_typed(string? typed, string? stored)
    {
        Assert.Equal(stored, PodFleetNumber.Normalize(typed));
    }

    [Fact]
    public void The_column_width_is_the_limit()
    {
        Assert.False(PodFleetNumber.IsTooLong(new string('1', PodFleetNumber.MaxLength)));
        Assert.True(PodFleetNumber.IsTooLong(new string('1', PodFleetNumber.MaxLength + 1)));
        Assert.False(PodFleetNumber.IsTooLong(null));
    }

    [Theory]
    [InlineData("Driver", "Cheeseman", true)]
    [InlineData("Driver", "Factory", false)]
    [InlineData("Merchandiser", "Cheeseman", false)]
    public async Task The_sign_in_reply_tells_the_app_whether_to_ask(string role, string section, bool expected)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "driver",
            PasswordHash = "x",
            Role = role,
            AssignedSection = section
        };

        var info = await UserInfoMapper.FromUserAsync(user, _context);

        Assert.Equal(expected, info.RequiresFleetNumber);
    }

    [Theory]
    // Without a reference the row goes through EF; with one it goes through the hand-written
    // INSERT ... ON CONFLICT, whose column list a new column has to be added to by hand.
    [InlineData(null)]
    [InlineData("MOBILE-POD-2232744-ABC")]
    public async Task The_fleet_number_is_kept_on_the_stored_pod(string? externalReference)
    {
        var service = CreateService();

        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4\n{externalReference}\n%%EOF"));
        var uploaded = await service.UploadAttachmentAsync(
            new UploadAttachmentRequest
            {
                EntityType = "Invoice",
                EntityId = 2232744,
                ExternalReference = externalReference,
                Description = "POD - Proof of Delivery",
                IncludeInEmail = false,
                FleetNumber = "CH 12"
            },
            stream,
            "POD_delivery.pdf",
            "application/pdf",
            null);

        _context.ChangeTracker.Clear();
        var stored = await _context.Set<DocumentAttachmentEntity>().SingleAsync(a => a.Id == uploaded.Id);
        Assert.Equal("CH 12", stored.FleetNumber);
        Assert.Equal("CH 12", uploaded.FleetNumber);
    }

    private DocumentService CreateService() =>
        new(
            _context,
            StubProxy.Unused<IEmailService>(),
            StubProxy.Unused<ISAPServiceLayerClient>(),
            NullLogger<DocumentService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            HybridLookupCacheTests.NewHybridCache(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:UploadPath"] = _uploadPath
                })
                .Build());
}
