using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartners;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnersByType;
using ShopInventory.Features.BusinessPartners.Queries.SearchBusinessPartners;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnerByCode;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnersByCodes;
using ShopInventory.Features.BusinessPartners.Queries.GetPaymentTerms;
using ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnerGroups;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class BusinessPartnerController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Upper bound on one batch request. Well above any real assigned-customer list, and low enough
    /// that a caller cannot turn a single request into an unbounded run of SAP reads.
    /// </summary>
    private const int MaxBatchCardCodes = 200;

    /// <summary>
    /// Get all business partners from SAP
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetBusinessPartners(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBusinessPartnersQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// SAP's business partner groups, so the group code cached against a partner can be named.
    /// </summary>
    [HttpGet("groups")]
    [ProducesResponseType(typeof(BusinessPartnerGroupsListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBusinessPartnerGroups(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBusinessPartnerGroupsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Filter by type
    /// </summary>
    [HttpGet("type/{cardType}")]
    public async Task<IActionResult> GetBusinessPartnersByType(string cardType, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBusinessPartnersByTypeQuery(cardType), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Search by code or name
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> SearchBusinessPartners([FromQuery] string q, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SearchBusinessPartnersQuery(q), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Resolves a known set of card codes in one call.
    /// <para>
    /// Declared before the <c>{cardCode}</c> route so "batch" is not swallowed as a card code.
    /// </para>
    /// </summary>
    /// <param name="cardCodes">
    /// Comma-separated card codes. Repeating the parameter works too, so both
    /// <c>?cardCodes=A,B</c> and <c>?cardCodes=A&amp;cardCodes=B</c> are accepted.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("batch")]
    [ProducesResponseType(typeof(BusinessPartnerListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBusinessPartnersByCodes(
        [FromQuery] string[]? cardCodes,
        CancellationToken cancellationToken)
    {
        var codes = (cardCodes ?? [])
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count > MaxBatchCardCodes)
        {
            return Problem(
            [
                Error.Validation(
                    "BusinessPartner.TooManyCodes",
                    $"At most {MaxBatchCardCodes} card codes can be requested at once.")
            ]);
        }

        var result = await mediator.Send(new GetBusinessPartnersByCodesQuery(codes), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get specific business partner
    /// </summary>
    [HttpGet("{cardCode}")]
    public async Task<IActionResult> GetBusinessPartnerByCode(string cardCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBusinessPartnerByCodeQuery(cardCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get payment terms by group
    /// </summary>
    [HttpGet("paymentterms/{groupNumber:int}")]
    public async Task<IActionResult> GetPaymentTerms(int groupNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPaymentTermsQuery(groupNumber), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
