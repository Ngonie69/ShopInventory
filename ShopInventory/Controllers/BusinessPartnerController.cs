using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartners;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnersByType;
using ShopInventory.Features.BusinessPartners.Queries.SearchBusinessPartners;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnerByCode;
using ShopInventory.Features.BusinessPartners.Queries.GetPaymentTerms;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class BusinessPartnerController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetBusinessPartners(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBusinessPartnersQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("type/{cardType}")]
    public async Task<IActionResult> GetBusinessPartnersByType(string cardType, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBusinessPartnersByTypeQuery(cardType), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchBusinessPartners([FromQuery] string q, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SearchBusinessPartnersQuery(q), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{cardCode}")]
    public async Task<IActionResult> GetBusinessPartnerByCode(string cardCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBusinessPartnerByCodeQuery(cardCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("paymentterms/{groupNumber:int}")]
    public async Task<IActionResult> GetPaymentTerms(int groupNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPaymentTermsQuery(groupNumber), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
