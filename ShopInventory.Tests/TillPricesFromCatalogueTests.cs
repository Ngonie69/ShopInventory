using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Controllers;
using ShopInventory.DTOs;
using ShopInventory.Features.Prices.Queries.GetPricesByBusinessPartner;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the till's price route answers from the local catalogue and never waits on SAP.
/// </summary>
/// <remarks>
/// Tills read a business partner's whole price list through <c>DesktopIntegration/prices/business-partner</c>
/// every time their products screen opens. Asked live, SAP's whole-list read ran past the 3-second
/// budget on 120 of 164 calls a KefShop till logged between 2026-09-08 and 09-13, so opening the
/// screen took three seconds and was then answered from the catalogue anyway. The stock read beside
/// it took a median of 117ms.
/// </remarks>
public sealed class TillPricesFromCatalogueTests
{
    private const string CardCode = "CIS006";

    [Fact]
    public async Task The_till_route_answers_from_the_catalogue_without_asking_SAP()
    {
        var sent = new List<object>();
        var controller = Controller(sent);

        var response = await controller.GetPricesByBusinessPartner(CardCode, CancellationToken.None);

        var query = Assert.IsType<GetPricesByBusinessPartnerQuery>(Assert.Single(sent));
        Assert.False(query.UseLivePricing);
        Assert.Equal(CardCode, query.CardCode);

        var body = Assert.IsType<ItemPricesByListResponseDto>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(107, body.PriceListNum);
        Assert.NotNull(body.Prices);
        Assert.Equal(0.22m, Assert.Single(body.Prices).Price);
    }

    /// <summary>
    /// Sends each request through the real handler, with an SAP client that fails the test if it is
    /// touched at all.
    /// </summary>
    private static DesktopIntegrationController Controller(List<object> sent)
    {
        var handler = new GetPricesByBusinessPartnerHandler(
            Catalogue(),
            StubProxy.Unused<ISAPServiceLayerClient>(),
            NullLogger<GetPricesByBusinessPartnerHandler>.Instance);

        var mediator = StubProxy.For<IMediator>((method, args) =>
        {
            if (method.Name != nameof(IMediator.Send) || args![0] is not GetPricesByBusinessPartnerQuery query)
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            sent.Add(query);
            return handler.Handle(query, (CancellationToken)args[1]!);
        });

        return new DesktopIntegrationController(mediator, StubProxy.Unused<IServiceScopeFactory>());
    }

    private static ILocalPriceCatalogService Catalogue() =>
        StubProxy.For<ILocalPriceCatalogService>((method, _) =>
            method.Name == nameof(ILocalPriceCatalogService.GetBusinessPartnerPricingAsync)
                ? Task.FromResult<LocalBusinessPartnerPricingResult?>(new LocalBusinessPartnerPricingResult
                {
                    BusinessPartner = new BusinessPartnerDto { CardCode = CardCode },
                    Prices = new ItemPricesByListResponseDto
                    {
                        TotalCount = 1,
                        PriceListNum = 107,
                        Currency = "USD",
                        Prices = [new ItemPriceByListDto { ItemCode = "BON001", Price = 0.22m }]
                    }
                })
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));
}
