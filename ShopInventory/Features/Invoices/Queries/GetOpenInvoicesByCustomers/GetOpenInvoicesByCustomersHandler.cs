using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.GetOpenInvoicesByCustomers;

public sealed class GetOpenInvoicesByCustomersHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetOpenInvoicesByCustomersHandler> logger
) : IRequestHandler<GetOpenInvoicesByCustomersQuery, ErrorOr<InvoiceDateResponseDto>>
{
    public async Task<ErrorOr<InvoiceDateResponseDto>> Handle(
        GetOpenInvoicesByCustomersQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
        {
            return Errors.Invoice.SapDisabled;
        }

        var cardCodes = request.CardCodes
            .Where(cardCode => !string.IsNullOrWhiteSpace(cardCode))
            .Select(cardCode => cardCode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cardCodes.Count == 0)
        {
            return Errors.Invoice.CustomerCodeRequired;
        }

        try
        {
            var invoices = await sapClient.GetOpenInvoicesByCustomersAsync(cardCodes, cancellationToken);

            logger.LogInformation(
                "Retrieved {Count} open invoice(s) for {AccountCount} account(s)",
                invoices.Count,
                cardCodes.Count);

            return new InvoiceDateResponseDto
            {
                Customer = string.Join(",", cardCodes),
                Page = 1,
                PageSize = invoices.Count,
                Count = invoices.Count,
                TotalCount = invoices.Count,
                TotalPages = invoices.Count > 0 ? 1 : 0,
                HasMore = false,
                Invoices = invoices.ToDto()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving open invoices for {CardCodes}", string.Join(",", cardCodes));
            return Errors.Invoice.RetrievalFailed(ex.Message);
        }
    }
}
