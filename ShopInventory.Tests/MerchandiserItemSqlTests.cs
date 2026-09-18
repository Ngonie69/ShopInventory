using System.Collections.Concurrent;
using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Merchandiser;
using ShopInventory.Features.Merchandiser.Commands.AssignProducts;
using ShopInventory.Features.Merchandiser.Commands.AssignProductsGlobal;
using ShopInventory.Features.Merchandiser.Commands.BackfillProductDetails;
using ShopInventory.Features.Merchandiser.Queries.GetActiveProducts;
using ShopInventory.Features.Merchandiser.Queries.GetActiveProductsByUser;
using ShopInventory.Features.Merchandiser.Queries.GetCustomerProducts;
using ShopInventory.Features.Merchandiser.Queries.GetGlobalProducts;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The merchandiser product handlers used to run <c>WHERE "ItemCode" IN (…)</c> under fixed SAP
/// SqlCodes, so the stored statement changed with every request — a 30–43 second PATCH on each
/// call, and two callers sharing a code could run each other's SQL. Two codes were each shared by
/// two handlers with different statements and fought over permanently.
/// </summary>
/// <remarks>
/// The statements now bind the item-code family as <c>:prefix</c> and filter locally. These pin
/// that down from the outside: whatever the request, a code is only ever sent one text, no text
/// carries request data, and the rows handed back are still exactly the requested ones in the
/// order the old <c>ORDER BY</c> gave.
/// </remarks>
public sealed class MerchandiserItemSqlTests : IDisposable
{
    private static readonly Guid Merchandiser = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public MerchandiserItemSqlTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
        context.Users.Add(new User
        {
            Id = Merchandiser,
            Username = "merch",
            PasswordHash = "not-a-real-hash",
            Role = "Merchandiser",
            IsActive = true
        });
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- the helper

    [Fact]
    public async Task The_first_bucket_runs_alone_before_the_rest_fan_out()
    {
        var firstCall = new TaskCompletionSource<List<Dictionary<string, object?>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();

        var sap = SapAnswering((prefix, _) =>
        {
            calls.Enqueue(prefix);
            return calls.Count == 1 ? firstCall.Task : Task.FromResult(new List<Dictionary<string, object?>>());
        });

        var run = sap.ExecuteForItemCodesAsync(
            "CODE", "name", "SQL", ["CHE011", "NRI049", "PIC003", "BUT015"], CancellationToken.None);

        // Held on the first bucket: nothing else may have been sent while it is unanswered.
        await Task.Delay(50);
        Assert.Equal(new[] { "BUT%" }, calls.ToArray());
        Assert.False(run.IsCompleted);

        firstCall.SetResult([]);
        await run;

        Assert.Equal(new[] { "BUT%", "CHE%", "NRI%", "PIC%" }, calls.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task No_codes_means_no_SAP_call()
    {
        var sap = StubProxy.Unused<ISAPServiceLayerClient>();

        Assert.Empty(await sap.ExecuteForItemCodesAsync("CODE", "name", "SQL", [], CancellationToken.None));
        Assert.Empty(await sap.ExecuteForItemCodesAsync("CODE", "name", "SQL", [" ", null, ""], CancellationToken.None));
    }

    [Fact]
    public async Task Rows_are_filtered_to_the_requested_codes_and_never_returned_twice()
    {
        var catalogue = new FakeCatalogue(
            Item("AB"), Item("ABC001"), Item("ABC002"), Item("ABD001"), Item("CHE011"));
        var sap = catalogue.Client();

        // AB% also returns ABC001; the ABC bucket returns it too. It must come back once.
        var rows = await sap.ExecuteForItemCodesAsync(
            "CODE", "name", "SQL", ["AB", "ABC001", "ABC001"], CancellationToken.None);

        Assert.Equal(["AB", "ABC001"], rows.Select(row => (string?)row["ItemCode"]));
    }

    [Fact]
    public async Task Code_matching_stays_exact_and_case_sensitive_like_the_IN_list()
    {
        var sap = new FakeCatalogue(Item("CHE011")).Client();

        var rows = await sap.ExecuteForItemCodesAsync("CODE", "name", "SQL", ["che011"], CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Other_parameters_are_bound_on_every_bucket_alongside_the_prefix()
    {
        var catalogue = new FakeCatalogue(Item("CHE011"), Item("NRI049"));
        var sap = catalogue.Client();

        await sap.ExecuteForItemCodesAsync(
            "CODE", "name", "SQL", ["CHE011", "NRI049"], CancellationToken.None,
            new Dictionary<string, string> { ["cardCode"] = "C001" });

        Assert.Equal(2, catalogue.Calls.Count);
        Assert.All(catalogue.Calls, call => Assert.Equal("C001", call.Parameters["cardCode"]));
        Assert.Equal(["CHE%", "NRI%"], catalogue.Calls.Select(call => call.Parameters["prefix"]).Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- the handlers

    /// <summary>
    /// Every handler, run over two different product sets, categories and customers. Each code
    /// must have been sent exactly one text, and no text may carry anything from the request.
    /// </summary>
    [Fact]
    public async Task Every_code_is_sent_one_constant_statement_whatever_the_request()
    {
        var catalogue = FullCatalogue();

        await RunEveryHandlerAsync(catalogue, ["CHE011", "CHE042", "NRI049"], ["PIC003"], "C001", "Cheese");
        ResetProducts();
        await RunEveryHandlerAsync(catalogue, ["PIC003", "BUT015"], ["CHE011", "ZZZ001"], "C002", "Rice");

        Assert.Empty(catalogue.UnexpectedCalls);

        var textsByCode = catalogue.Calls
            .GroupBy(call => call.Code)
            .ToDictionary(group => group.Key, group => group.Select(call => call.Sql).Distinct().ToList());

        // Every statement in MerchandiserItemSql was exercised...
        Assert.Equal(
            new[] { MerchandiserItemSql.ItemDetailsCode, MerchandiserItemSql.ProductsListOneCode, MerchandiserItemSql.ProductsForCustomerCode }.Order(StringComparer.Ordinal),
            textsByCode.Keys.Order(StringComparer.Ordinal));

        // ...each code saw exactly one text, and that text is the declared one.
        Assert.Equal(MerchandiserItemSql.ItemDetailsSql, Assert.Single(textsByCode[MerchandiserItemSql.ItemDetailsCode]));
        Assert.Equal(MerchandiserItemSql.ProductsListOneSql, Assert.Single(textsByCode[MerchandiserItemSql.ProductsListOneCode]));
        Assert.Equal(MerchandiserItemSql.ProductsForCustomerSql, Assert.Single(textsByCode[MerchandiserItemSql.ProductsForCustomerCode]));

        // No two codes hold the same text either, so no statement is stored twice.
        Assert.Equal(textsByCode.Count, textsByCode.Values.Select(texts => texts[0]).Distinct().Count());

        foreach (var sql in textsByCode.Values.Select(texts => texts[0]))
        {
            foreach (var requestValue in new[] { "CHE", "NRI", "PIC", "BUT", "ZZZ", "C001", "C002", "Cheese", "Rice" })
            {
                Assert.DoesNotContain(requestValue, sql, StringComparison.Ordinal);
            }

            Assert.Contains("LIKE :prefix", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(" IN ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FROM OITM T0", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Customer_products_are_exactly_the_active_codes_in_item_name_order()
    {
        SeedProducts(["CHE011", "CHE042", "NRI049", "PIC003"]);
        var catalogue = FullCatalogue();

        var products = await CustomerProducts(catalogue).Handle(
            new GetCustomerProductsQuery(Merchandiser, "C001", Search: null, Category: null), CancellationToken.None);

        // CHE099 and NRI050 share buckets with requested codes and must not leak through. NRI049
        // has no name, and HANA sorts nulls first ascending.
        Assert.Equal(["NRI049", "CHE042", "CHE011", "PIC003"], products.Value.Select(product => product.ItemCode));
        Assert.Equal("Cheddar", products.Value[1].ItemName);
        Assert.Equal(7.5m, products.Value[1].Price);
        Assert.Equal("BC042", products.Value[1].BarCode);
        Assert.Equal("Cheese", products.Value[1].Category);

        Assert.All(catalogue.Calls, call =>
        {
            Assert.Equal(MerchandiserItemSql.ProductsForCustomerCode, call.Code);
            Assert.Equal("C001", call.Parameters[MerchandiserItemSql.CardCodeParameter]);
        });
    }

    [Fact]
    public async Task Customer_products_without_a_card_code_use_price_list_one()
    {
        SeedProducts(["CHE011"]);
        var catalogue = FullCatalogue();

        await CustomerProducts(catalogue).Handle(
            new GetCustomerProductsQuery(Merchandiser, "", Search: null, Category: null), CancellationToken.None);

        var call = Assert.Single(catalogue.Calls);
        Assert.Equal(MerchandiserItemSql.ProductsListOneCode, call.Code);
        Assert.False(call.Parameters.ContainsKey(MerchandiserItemSql.CardCodeParameter));
    }

    [Fact]
    public async Task The_category_filter_is_exact_and_applied_locally()
    {
        SeedProducts(["CHE011", "CHE042", "NRI049", "PIC003"]);
        var catalogue = FullCatalogue();

        var cheese = await CustomerProducts(catalogue).Handle(
            new GetCustomerProductsQuery(Merchandiser, "C001", Search: null, Category: "Cheese"), CancellationToken.None);
        var lowerCase = await CustomerProducts(catalogue).Handle(
            new GetCustomerProductsQuery(Merchandiser, "C001", Search: null, Category: "cheese"), CancellationToken.None);

        Assert.Equal(["CHE042", "CHE011"], cheese.Value.Select(product => product.ItemCode));
        // HANA's '=' on U_ItemGroup was case-sensitive.
        Assert.Empty(lowerCase.Value);
    }

    [Fact]
    public async Task Products_by_user_keep_search_and_item_name_order()
    {
        SeedProducts(["CHE011", "CHE042", "NRI049", "PIC003"]);
        var catalogue = FullCatalogue();
        var handler = new GetActiveProductsByUserHandler(
            NewContext(), catalogue.Client(), NullLogger<GetActiveProductsByUserHandler>.Instance);

        var all = await handler.Handle(new GetActiveProductsByUserQuery(Merchandiser, null, null), CancellationToken.None);
        var searched = await handler.Handle(new GetActiveProductsByUserQuery(Merchandiser, "ch", null), CancellationToken.None);

        Assert.Equal(["NRI049", "CHE042", "CHE011", "PIC003"], all.Value.Select(product => product.ItemCode));
        Assert.Equal(["CHE042", "CHE011"], searched.Value.Select(product => product.ItemCode));
        Assert.Equal(1m, all.Value[1].Price);
        Assert.All(catalogue.Calls, call => Assert.Equal(MerchandiserItemSql.ProductsListOneCode, call.Code));
    }

    [Fact]
    public async Task Assignment_reads_the_details_from_the_shared_statement_columns()
    {
        var catalogue = FullCatalogue();
        var handler = new AssignProductsHandler(
            NewContext(), AnsweringSender(), catalogue.Client(), NullLogger<AssignProductsHandler>.Instance);

        await handler.Handle(
            new AssignProductsCommand(Merchandiser, new AssignMerchandiserProductsRequest { ItemCodes = ["CHE042"] }, "tester"),
            CancellationToken.None);

        using var context = NewContext();
        var assigned = Assert.Single(context.MerchandiserProducts);
        Assert.Equal("Cheddar", assigned.ItemName);
        Assert.Equal("BC042", assigned.BarCode);
        Assert.Equal("EA", assigned.UoM);
        Assert.Equal("Cheese", assigned.Category);
    }

    // ---------------------------------------------------------------- fixtures

    private async Task RunEveryHandlerAsync(
        FakeCatalogue catalogue,
        string[] activeCodes,
        string[] assignCodes,
        string cardCode,
        string category)
    {
        SeedProducts(activeCodes);

        await CustomerProducts(catalogue).Handle(new GetCustomerProductsQuery(Merchandiser, cardCode, null, category), CancellationToken.None);
        await CustomerProducts(catalogue).Handle(new GetCustomerProductsQuery(Merchandiser, "", null, null), CancellationToken.None);

        await new GetActiveProductsByUserHandler(NewContext(), catalogue.Client(), NullLogger<GetActiveProductsByUserHandler>.Instance)
            .Handle(new GetActiveProductsByUserQuery(Merchandiser, null, category), CancellationToken.None);

        await new GetActiveProductsHandler(NewContext(), catalogue.Client(), SilentAudit(), NullLogger<GetActiveProductsHandler>.Instance)
            .Handle(new GetActiveProductsQuery(Merchandiser, null, category), CancellationToken.None);

        ClearDenormalisedFields();
        await new GetGlobalProductsHandler(NewContext(), catalogue.Client(), NullLogger<GetGlobalProductsHandler>.Instance)
            .Handle(new GetGlobalProductsQuery(), CancellationToken.None);

        ClearDenormalisedFields();
        await new BackfillProductDetailsHandler(NewContext(), catalogue.Client(), NullLogger<BackfillProductDetailsHandler>.Instance)
            .Handle(new BackfillProductDetailsCommand(), CancellationToken.None);

        await new AssignProductsHandler(NewContext(), AnsweringSender(), catalogue.Client(), NullLogger<AssignProductsHandler>.Instance)
            .Handle(new AssignProductsCommand(Merchandiser, new AssignMerchandiserProductsRequest { ItemCodes = [.. assignCodes] }, "tester"), CancellationToken.None);

        await new AssignProductsGlobalHandler(NewContext(), AnsweringSender(), catalogue.Client(), NullLogger<AssignProductsGlobalHandler>.Instance)
            .Handle(new AssignProductsGlobalCommand(new AssignMerchandiserProductsRequest { ItemCodes = [.. assignCodes] }, "tester"), CancellationToken.None);
    }

    private GetCustomerProductsHandler CustomerProducts(FakeCatalogue catalogue) =>
        new(NewContext(), catalogue.Client(), SilentAudit(), NullLogger<GetCustomerProductsHandler>.Instance);

    private ApplicationDbContext NewContext() => new(_options);

    private void SeedProducts(IEnumerable<string> codes)
    {
        using var context = NewContext();
        context.MerchandiserProducts.AddRange(codes.Select(code => new MerchandiserProductEntity
        {
            MerchandiserUserId = Merchandiser,
            ItemCode = code,
            IsActive = true
        }));
        context.SaveChanges();
    }

    private void ResetProducts()
    {
        using var context = NewContext();
        context.MerchandiserProducts.RemoveRange(context.MerchandiserProducts);
        context.SaveChanges();
    }

    private void ClearDenormalisedFields()
    {
        using var context = NewContext();
        foreach (var product in context.MerchandiserProducts)
        {
            product.ItemName = null;
            product.Category = null;
            product.BarCode = null;
            product.UoM = null;
        }

        context.SaveChanges();
    }

    private static IAuditService SilentAudit() =>
        StubProxy.For<IAuditService>((_, _) => Task.CompletedTask);

    private static ISender AnsweringSender() =>
        StubProxy.For<ISender>((_, _) =>
            Task.FromResult<ErrorOr<MerchandiserProductListResponseDto>>(new MerchandiserProductListResponseDto()));

    private static ISAPServiceLayerClient SapAnswering(
        Func<string, IReadOnlyDictionary<string, string>, Task<List<Dictionary<string, object?>>>> answer) =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) =>
            method.Name == nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync)
                ? answer(((IReadOnlyDictionary<string, string>)args![3]!)["prefix"], (IReadOnlyDictionary<string, string>)args[3]!)
                : throw new InvalidOperationException($"Unexpected SAP call {method.Name}"));

    private static CatalogueItem Item(string code, string? name = null, string? category = null, decimal price = 0m) =>
        new(code, name, category, price);

    /// <summary>
    /// Catalogue rows in deliberately non-alphabetical order, with unrequested neighbours in the
    /// same families (CHE099, NRI050) so a missing local filter shows.
    /// </summary>
    private static FakeCatalogue FullCatalogue() => new(
        Item("PIC003", "Pickles", "Condiments", 3m),
        Item("CHE099", "Aaa unrequested", "Cheese", 9m),
        Item("CHE011", "Gouda", "Cheese", 5m),
        Item("NRI049", null, "Rice", 2m),
        Item("CHE042", "Cheddar", "Cheese", 7.5m),
        Item("NRI050", "Basmati", "Rice", 4m),
        Item("BUT015", "Butter", "Dairy", 6m),
        Item("ZZZ001", "Last", "Other", 1m));

    private sealed record CatalogueItem(string Code, string? Name, string? Category, decimal CustomerPrice);

    private sealed record SqlCall(string Code, string Sql, IReadOnlyDictionary<string, string> Parameters);

    /// <summary>
    /// Answers the parameterised path the way SAP would for these statements: every item whose code
    /// starts with the bound prefix (LIKE is case-sensitive on HANA), columns shaped per statement.
    /// Price list 1 prices everything at 1; a customer price list at the item's own price.
    /// </summary>
    private sealed class FakeCatalogue(params CatalogueItem[] items)
    {
        public ConcurrentQueue<SqlCall> Calls { get; } = new();

        public ConcurrentQueue<string> UnexpectedCalls { get; } = new();

        public ISAPServiceLayerClient Client() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) =>
            {
                if (method.Name != nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync))
                {
                    UnexpectedCalls.Enqueue(method.Name);
                    throw new InvalidOperationException($"Unexpected SAP call {method.Name}");
                }

                var code = (string)args![0]!;
                var sql = (string)args[2]!;
                var parameters = (IReadOnlyDictionary<string, string>)args[3]!;
                Calls.Enqueue(new SqlCall(code, sql, parameters));

                var prefix = parameters["prefix"].TrimEnd('%');
                var rows = items
                    .Where(item => item.Code.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(item => Row(code, item, parameters.ContainsKey("cardCode")))
                    .ToList();

                return Task.FromResult(rows);
            });

        private static Dictionary<string, object?> Row(string code, CatalogueItem item, bool customerPriced)
        {
            var barCode = "BC" + (item.Code.Length > 3 ? item.Code[3..] : item.Code);

            return code == MerchandiserItemSql.ItemDetailsCode
                ? new Dictionary<string, object?>
                {
                    ["ItemCode"] = item.Code,
                    ["ItemName"] = item.Name,
                    ["CodeBars"] = barCode,
                    ["SalUnitMsr"] = "EA",
                    ["U_ItemGroup"] = item.Category
                }
                : new Dictionary<string, object?>
                {
                    ["ItemCode"] = item.Code,
                    ["ItemName"] = item.Name,
                    ["BarCode"] = barCode,
                    ["UoM"] = "EA",
                    ["InventoryUOM"] = "EA",
                    ["Category"] = item.Category,
                    ["Price"] = customerPriced ? item.CustomerPrice : 1m
                };
        }
    }
}
