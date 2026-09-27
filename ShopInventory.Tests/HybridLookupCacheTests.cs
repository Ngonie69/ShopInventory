using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes.Queries.GetCreditNoteReasons;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The three lookups moved from IMemoryCache to HybridCache. Each must keep what it did before —
/// cache a success, never cache a failure, hand back the same values on a hit — and gain the one
/// thing it lacked: requests that miss together make one upstream call, not one each.
/// </summary>
public class HybridLookupCacheTests
{
    private const int Callers = 8;

    internal static HybridCache NewHybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    /// <summary>
    /// Counts loader calls and holds each one open until released, so every caller is waiting on
    /// the cache before the first load can finish.
    /// </summary>
    private sealed class GatedLoader<T>(Func<T> answer)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public bool Fail { get; set; }

        public void Release() => _release.TrySetResult();

        public async Task<T> LoadAsync()
        {
            Interlocked.Increment(ref _calls);
            await _release.Task;
            return Fail ? throw new HttpRequestException("upstream down") : answer();
        }
    }

    private static async Task<TResult[]> MissTogether<TAnswer, TResult>(
        GatedLoader<TAnswer> loader,
        Func<Task<TResult>> call)
    {
        var calls = Enumerable.Range(0, Callers).Select(_ => Task.Run(call)).ToArray();

        // Give every caller time to reach the cache before the first load is allowed to finish.
        await Task.Delay(200);
        loader.Release();
        return await Task.WhenAll(calls);
    }

    // ---- Fiscal device configuration -------------------------------------------------------

    private static FiscalConfigApiResponse DeviceConfig() => new()
    {
        DeviceSerialNo = "SN-001",
        QrUrl = "https://fdms.example/verify",
        CertificateValidTill = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        ApplicableTaxes = [new FiscalTaxDto { TaxID = 1, TaxPercent = 15.5m, TaxName = "VAT" }]
    };

    private static FiscalDeviceConfigCache ConfigCache(GatedLoader<FiscalConfigApiResponse> loader, HybridCache cache) =>
        new(
            StubProxy.For<IFiscalisationApiClient>((method, _) =>
                method.Name == nameof(IFiscalisationApiClient.GetFiscalConfigAsync) ? loader.LoadAsync() : null),
            cache,
            NullLogger<FiscalDeviceConfigCache>.Instance);

    [Fact]
    public async Task Fiscal_config_misses_together_share_one_platform_call()
    {
        var loader = new GatedLoader<FiscalConfigApiResponse>(DeviceConfig);
        var cache = NewHybridCache();

        var results = await MissTogether(loader, () => ConfigCache(loader, cache).TryGetAsync(7));

        Assert.Equal(1, loader.Calls);
        Assert.All(results, config => Assert.Equal("https://fdms.example/verify", config!.QrUrl));
    }

    [Fact]
    public async Task Fiscal_config_hit_carries_every_field_the_receipt_needs()
    {
        var loader = new GatedLoader<FiscalConfigApiResponse>(DeviceConfig);
        loader.Release();
        var cache = NewHybridCache();

        await ConfigCache(loader, cache).TryGetAsync(7);
        var hit = await ConfigCache(loader, cache).TryGetAsync(7);

        Assert.Equal(1, loader.Calls);
        Assert.Equal("SN-001", hit!.DeviceSerialNo);
        Assert.Equal("https://fdms.example/verify", hit.QrUrl);
        Assert.Equal(new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc), hit.CertificateValidTill);
        var tax = Assert.Single(hit.ApplicableTaxes);
        Assert.Equal(15.5m, tax.TaxPercent);
    }

    [Fact]
    public async Task Fiscal_config_failure_returns_null_and_is_not_cached()
    {
        var loader = new GatedLoader<FiscalConfigApiResponse>(DeviceConfig) { Fail = true };
        loader.Release();
        var cache = NewHybridCache();

        Assert.Null(await ConfigCache(loader, cache).TryGetAsync(7));

        loader.Fail = false;
        var recovered = await ConfigCache(loader, cache).TryGetAsync(7);

        Assert.Equal(2, loader.Calls);
        Assert.Equal("SN-001", recovered!.DeviceSerialNo);
    }

    [Fact]
    public async Task Fiscal_config_is_cached_per_device()
    {
        var loader = new GatedLoader<FiscalConfigApiResponse>(DeviceConfig);
        loader.Release();
        var cache = NewHybridCache();

        await ConfigCache(loader, cache).TryGetAsync(7);
        await ConfigCache(loader, cache).TryGetAsync(8);

        Assert.Equal(2, loader.Calls);
    }

    // ---- Credit note reasons ---------------------------------------------------------------

    private static IReadOnlyList<SapDocumentLineReason> Reasons() =>
        [new("DMG", "Damaged in transit"), new("EXP", "Expired")];

    private static GetCreditNoteReasonsHandler ReasonsHandler(
        GatedLoader<IReadOnlyList<SapDocumentLineReason>> loader,
        HybridCache cache) =>
        new(
            StubProxy.For<ISAPServiceLayerClient>((method, _) =>
                method.Name == nameof(ISAPServiceLayerClient.GetCreditNoteLineReasonsAsync) ? loader.LoadAsync() : null),
            cache,
            NullLogger<GetCreditNoteReasonsHandler>.Instance);

    [Fact]
    public async Task Credit_note_reasons_misses_together_share_one_SAP_call()
    {
        var loader = new GatedLoader<IReadOnlyList<SapDocumentLineReason>>(Reasons);
        var cache = NewHybridCache();

        var results = await MissTogether(
            loader,
            () => ReasonsHandler(loader, cache).Handle(new GetCreditNoteReasonsQuery(), CancellationToken.None));

        Assert.Equal(1, loader.Calls);
        Assert.All(results, result => Assert.Equal(2, result.Value.Reasons.Count));
    }

    [Fact]
    public async Task Credit_note_reasons_hit_keeps_SAP_order_and_wording()
    {
        var loader = new GatedLoader<IReadOnlyList<SapDocumentLineReason>>(Reasons);
        loader.Release();
        var cache = NewHybridCache();

        await ReasonsHandler(loader, cache).Handle(new GetCreditNoteReasonsQuery(), CancellationToken.None);
        var hit = await ReasonsHandler(loader, cache).Handle(new GetCreditNoteReasonsQuery(), CancellationToken.None);

        Assert.Equal(1, loader.Calls);
        Assert.Equal(
            [new CreditNoteReasonOption("DMG", "Damaged in transit"), new CreditNoteReasonOption("EXP", "Expired")],
            hit.Value.Reasons);
    }

    [Fact]
    public async Task Credit_note_reasons_empty_list_is_cached()
    {
        var loader = new GatedLoader<IReadOnlyList<SapDocumentLineReason>>(() => []);
        loader.Release();
        var cache = NewHybridCache();

        await ReasonsHandler(loader, cache).Handle(new GetCreditNoteReasonsQuery(), CancellationToken.None);
        var hit = await ReasonsHandler(loader, cache).Handle(new GetCreditNoteReasonsQuery(), CancellationToken.None);

        Assert.Equal(1, loader.Calls);
        Assert.Empty(hit.Value.Reasons);
    }

    [Fact]
    public async Task Credit_note_reasons_failure_is_an_error_and_is_not_cached()
    {
        var loader = new GatedLoader<IReadOnlyList<SapDocumentLineReason>>(Reasons) { Fail = true };
        loader.Release();
        var cache = NewHybridCache();

        var failed = await ReasonsHandler(loader, cache).Handle(new GetCreditNoteReasonsQuery(), CancellationToken.None);
        Assert.True(failed.IsError);

        loader.Fail = false;
        var recovered = await ReasonsHandler(loader, cache).Handle(new GetCreditNoteReasonsQuery(), CancellationToken.None);

        Assert.Equal(2, loader.Calls);
        Assert.Equal(2, recovered.Value.Reasons.Count);
    }

    // ---- POD warehouse codes ---------------------------------------------------------------

    private const string Section = "TESTSECTION";

    private static List<WarehouseDto> Warehouses() =>
    [
        new() { WarehouseCode = "wh01", Location = Section },
        new() { WarehouseCode = "WH02", Location = "ELSEWHERE" }
    ];

    private static DocumentService PodService(GatedLoader<List<WarehouseDto>> loader, HybridCache cache) =>
        new(
            context: null!,
            emailService: null!,
            StubProxy.For<ISAPServiceLayerClient>((method, _) =>
                method.Name == nameof(ISAPServiceLayerClient.GetWarehousesAsync) ? loader.LoadAsync() : null),
            NullLogger<DocumentService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            cache,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:UploadPath"] = Path.GetTempPath() })
                .Build());

    [Fact]
    public async Task Pod_warehouse_codes_misses_together_share_one_SAP_call()
    {
        var loader = new GatedLoader<List<WarehouseDto>>(Warehouses);
        var cache = NewHybridCache();

        var results = await MissTogether(
            loader,
            () => PodService(loader, cache).GetPodWarehouseCodesForAssignedSectionAsync(Section, CancellationToken.None));

        Assert.Equal(1, loader.Calls);
        Assert.All(results, codes => Assert.Equal(["WH01"], codes));
    }

    [Fact]
    public async Task Pod_warehouse_codes_fallback_is_not_cached()
    {
        var loader = new GatedLoader<List<WarehouseDto>>(Warehouses) { Fail = true };
        loader.Release();
        var cache = NewHybridCache();

        var fallback = await PodService(loader, cache).GetPodWarehouseCodesForAssignedSectionAsync(Section, CancellationToken.None);
        Assert.Equal([Section], fallback);

        loader.Fail = false;
        var recovered = await PodService(loader, cache).GetPodWarehouseCodesForAssignedSectionAsync(Section, CancellationToken.None);
        var hit = await PodService(loader, cache).GetPodWarehouseCodesForAssignedSectionAsync(Section, CancellationToken.None);

        Assert.Equal(2, loader.Calls);
        Assert.Equal(["WH01"], recovered);
        Assert.Equal(["WH01"], hit);
    }
}
