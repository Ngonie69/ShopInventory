using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;
using SkiaSharp;

namespace ShopInventory.Tests;

/// <summary>
/// What an uploaded photo becomes on disk: a JPEG no larger than 1920 on its long side, turned the
/// way the camera's EXIF orientation says.
/// </summary>
/// <remarks>
/// The fixtures are left half red, right half blue, so where each colour lands says which way the
/// picture was turned. They were made once and are checked in, so the same bytes are fed to every
/// implementation of the compression.
/// </remarks>
public sealed class AttachmentImageCompressionTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly string _uploadRoot;

    public AttachmentImageCompressionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options);
        _context.Database.EnsureCreated();

        _uploadRoot = Path.Combine(Path.GetTempPath(), $"shopinv-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_uploadRoot);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();

        try
        {
            Directory.Delete(_uploadRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_large_jpeg_is_shrunk_to_1920_on_its_long_side()
    {
        var stored = await UploadAsync(Fixture("split-2400x1200.jpg"), "image/jpeg");

        Assert.Equal((1920, 960), (stored.Width, stored.Height));
        AssertRedThenBlue(stored, horizontal: true);
    }

    [Theory]
    [InlineData(3, 1920, 960, "blue-red")]   // upside down: the halves swap
    [InlineData(6, 960, 1920, "red-blue")]   // rotate 90° clockwise: the left half goes to the top
    [InlineData(8, 960, 1920, "blue-red")]   // rotate 90° anticlockwise: the left half goes to the bottom
    public async Task A_photo_is_turned_the_way_its_EXIF_orientation_says(
        int orientation, int width, int height, string order)
    {
        var stored = await UploadAsync(WithExifOrientation(Fixture("split-2400x1200.jpg"), orientation), "image/jpeg");

        Assert.Equal((width, height), (stored.Width, stored.Height));

        var horizontal = width > height;
        if (order == "red-blue")
        {
            AssertRedThenBlue(stored, horizontal);
        }
        else
        {
            AssertBlueThenRed(stored, horizontal);
        }
    }

    [Fact]
    public async Task A_small_png_keeps_its_size_and_is_stored_as_jpeg()
    {
        var stored = await UploadAsync(Fixture("split-800x400.png"), "image/png");

        Assert.Equal((800, 400), (stored.Width, stored.Height));
        Assert.Equal("image/jpeg", stored.Attachment.MimeType);
        Assert.EndsWith(".jpg", stored.Path, StringComparison.Ordinal);
        AssertRedThenBlue(stored, horizontal: true);
    }

    [Fact]
    public async Task A_webp_is_shrunk_and_stored_as_jpeg()
    {
        var stored = await UploadAsync(Fixture("split-3000x1000.webp"), "image/webp");

        Assert.Equal((1920, 640), (stored.Width, stored.Height));
        AssertRedThenBlue(stored, horizontal: true);
    }

    /// <summary>
    /// JPEG has no transparency, so a transparent pixel has to become some colour. Black is what the
    /// ImageSharp implementation produced, and what this one keeps.
    /// </summary>
    [Fact]
    public async Task Characterisation_transparent_pixels_are_stored_black()
    {
        var stored = await UploadAsync(Fixture("red-and-transparent-400x200.png"), "image/png");

        AssertRed(stored.At(0.25, 0.5));
        var transparent = stored.At(0.75, 0.5);
        Assert.True(transparent is { R: < 30, G: < 30, B: < 30 }, $"expected black, got {transparent}");
    }

    /// <summary>
    /// Bytes that are not an image, and a TIFF sent as a JPEG, are refused as bad input (a 400 through
    /// RequestInputExceptionHandler) and nothing is written. The TIFF is the shape of the 2026-10 ImageSharp
    /// advisories: a decoder reached by content sniffing that the upload never meant to offer.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 0x2B, 0x00, 0x08, 0x00, 0x00, 0x00, 0x10, 0, 0, 0, 0, 0, 0, 0 })] // BigTIFF header
    public async Task Bytes_that_are_not_an_accepted_image_are_refused_and_nothing_is_stored(byte[] bytes)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => UploadAsync(bytes, "image/jpeg"));

        Assert.Empty(Directory.GetFiles(_uploadRoot, "*", SearchOption.AllDirectories));
    }

    // --- Helpers ---

    private sealed record StoredImage(
        DocumentAttachmentDto Attachment,
        string Path,
        int Width,
        int Height,
        Func<double, double, (int R, int G, int B)> At);

    private async Task<StoredImage> UploadAsync(byte[] bytes, string mimeType)
    {
        var service = new DocumentService(
            _context,
            emailService: null!,
            sapServiceLayerClient: null!,
            NullLogger<DocumentService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            HybridLookupCacheTests.NewHybridCache(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:UploadPath"] = _uploadRoot })
                .Build());

        using var payload = new MemoryStream(bytes);
        var attachment = await service.UploadAttachmentAsync(
            new UploadAttachmentRequest { EntityType = "Invoice", EntityId = 4242 },
            payload,
            "photo." + mimeType["image/".Length..],
            mimeType,
            userId: null,
            CancellationToken.None);

        var path = Assert.Single(Directory.GetFiles(_uploadRoot, "*", SearchOption.AllDirectories));
        var (width, height, at) = Decode(path);
        return new StoredImage(attachment, path, width, height, at);
    }

    /// <summary>Reads a stored image back: its size, and the colour at a point given as fractions of it.</summary>
    private static (int Width, int Height, Func<double, double, (int R, int G, int B)> At) Decode(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = bitmap.Pixels;

        return (width, height, (fx, fy) =>
        {
            var p = pixels[(int)(fy * height) * width + (int)(fx * width)];
            return (p.Red, p.Green, p.Blue);
        });
    }

    private static void AssertRedThenBlue(StoredImage image, bool horizontal)
    {
        AssertRed(horizontal ? image.At(0.25, 0.5) : image.At(0.5, 0.25));
        AssertBlue(horizontal ? image.At(0.75, 0.5) : image.At(0.5, 0.75));
    }

    private static void AssertBlueThenRed(StoredImage image, bool horizontal)
    {
        AssertBlue(horizontal ? image.At(0.25, 0.5) : image.At(0.5, 0.25));
        AssertRed(horizontal ? image.At(0.75, 0.5) : image.At(0.5, 0.75));
    }

    private static void AssertRed((int R, int G, int B) c) =>
        Assert.True(c.R > 180 && c.G < 70 && c.B < 70, $"expected red, got {c}");

    private static void AssertBlue((int R, int G, int B) c) =>
        Assert.True(c.B > 180 && c.R < 70 && c.G < 70, $"expected blue, got {c}");

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, name));

    /// <summary>
    /// The JPEG with an EXIF segment carrying only an orientation, put straight after the start-of-image
    /// marker, where a camera writes it.
    /// </summary>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian header, IFD at 8
            0x01, 0x00,                                               // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,           // Orientation, SHORT, count 1
            (byte)orientation, 0x00, 0x00, 0x00,                      // the value, padded
            0x00, 0x00, 0x00, 0x00                                    // no next IFD
        ];

        byte[] exifHeader = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00];
        var length = 2 + exifHeader.Length + tiff.Length;

        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2); // FFD8
        output.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        output.Write(exifHeader);
        output.Write(tiff);
        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }
}
