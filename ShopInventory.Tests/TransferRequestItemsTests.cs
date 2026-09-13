using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestItems;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the list a till may raise a transfer request from: SAP's sales items, read with the given
/// statement, held for a while, and still served when SAP cannot be reached.
/// </summary>
public class TransferRequestItemsTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly List<string> _statements = [];
    private Func<List<Dictionary<string, object?>>> _answer = () => [];

    [Fact]
    public async Task Every_sales_item_is_offered_once_with_its_name()
    {
        _answer = () =>
        [
            Row(" ICV014 ", "1 Litre Vanilla Cortina Icecream"),
            Row("CHE001", "Feta 200g"),
            Row("CHE001", "Feta 200g"),
            Row("", "No code"),
            Row("RAW900", null),
        ];

        var result = await CreateHandler().Handle(new GetTransferRequestItemsQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            [
                new TransferRequestItemDto("CHE001", "Feta 200g"),
                new TransferRequestItemDto("ICV014", "1 Litre Vanilla Cortina Icecream"),
                new TransferRequestItemDto("RAW900", "RAW900"),
            ],
            result.Value.Items);
        Assert.Equal([GetTransferRequestItemsHandler.SqlText], _statements);
        Assert.Contains("T0.\"U_SalesItem\" ='Yes'", GetTransferRequestItemsHandler.SqlText);
    }

    [Fact]
    public async Task A_second_screen_within_the_hour_does_not_ask_SAP_again()
    {
        _answer = () => [Row("CHE001", "Feta 200g")];
        var handler = CreateHandler();

        await handler.Handle(new GetTransferRequestItemsQuery(), CancellationToken.None);
        await handler.Handle(new GetTransferRequestItemsQuery(), CancellationToken.None);

        Assert.Single(_statements);
    }

    [Fact]
    public async Task A_SAP_outage_serves_the_last_list_read()
    {
        _answer = () => [Row("CHE001", "Feta 200g")];
        var handler = CreateHandler();
        await handler.Handle(new GetTransferRequestItemsQuery(), CancellationToken.None);

        // The hour is up and SAP is down.
        _cache.Remove(GetTransferRequestItemsHandler.FreshKey);
        _answer = () => throw new HttpRequestException("SAP unreachable");

        var result = await handler.Handle(new GetTransferRequestItemsQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("CHE001", Assert.Single(result.Value.Items).ItemCode);
        Assert.Equal(2, _statements.Count);
    }

    [Fact]
    public async Task A_SAP_outage_with_nothing_read_yet_is_an_error()
    {
        _answer = () => throw new HttpRequestException("SAP unreachable");

        var result = await CreateHandler().Handle(new GetTransferRequestItemsQuery(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopIntegration.SapError", result.FirstError.Code);
    }

    private GetTransferRequestItemsHandler CreateHandler()
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            if (method.Name != nameof(ISAPServiceLayerClient.ExecuteRawSqlQueryAsync) || args!.Length != 4)
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            _statements.Add((string)args[2]!);
            try
            {
                return Task.FromResult(_answer());
            }
            catch (Exception ex)
            {
                return Task.FromException<List<Dictionary<string, object?>>>(ex);
            }
        });

        return new GetTransferRequestItemsHandler(sap, _cache, NullLogger<GetTransferRequestItemsHandler>.Instance);
    }

    private static Dictionary<string, object?> Row(string code, string? name) => new()
    {
        ["ItemCode"] = code,
        ["ItemName"] = name,
        ["U_SalesItem"] = "Yes",
    };
}
