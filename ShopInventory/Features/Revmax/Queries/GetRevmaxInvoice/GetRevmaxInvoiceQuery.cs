using ErrorOr;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Queries.GetRevmaxInvoice;

/// <summary>REVMax's <c>GetInvoice/{invoiceNumber}</c>, as the device answers it.</summary>
public sealed record GetRevmaxInvoiceQuery(string InvoiceNumber) : IRequest<ErrorOr<InvoiceResponse>>;

public sealed class GetRevmaxInvoiceValidator : AbstractValidator<GetRevmaxInvoiceQuery>
{
    public GetRevmaxInvoiceValidator()
    {
        RuleFor(x => x.InvoiceNumber)
            .NotEmpty().WithMessage("Invoice number is required")
            .MaximumLength(100);
    }
}

/// <remarks>
/// The box serves several devices and answers a number with whichever device's receipt carries it, so
/// the response is returned whole: <c>DeviceID</c> says whose receipt it is.
/// </remarks>
public sealed class GetRevmaxInvoiceHandler(
    IRevmaxClient client,
    IOptions<RevmaxSettings> settings,
    ILogger<GetRevmaxInvoiceHandler> logger
) : IRequestHandler<GetRevmaxInvoiceQuery, ErrorOr<InvoiceResponse>>
{
    public async Task<ErrorOr<InvoiceResponse>> Handle(GetRevmaxInvoiceQuery query, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Revmax.Disabled;

        var number = query.InvoiceNumber.Trim();
        InvoiceResponse? response;
        try
        {
            response = await client.GetInvoiceAsync(number, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "REVMax GetInvoice for {InvoiceNumber} failed", number);
            return Errors.Revmax.DeviceUnavailable(number, ex.Message);
        }

        return Classify(number, response);
    }

    /// <summary>
    /// The device's busy state comes back in the same shape as "Invoice not Found" (Code "0", no data).
    /// Only the second is an answer. See RevmaxFiscalReceiptReader.
    /// </summary>
    internal static ErrorOr<InvoiceResponse> Classify(string number, InvoiceResponse? response) =>
        response switch
        {
            null => Errors.Revmax.DeviceUnavailable(number, "the device returned no answer"),
            { Success: true } => response,
            _ when IsNotFound(response) => Errors.Revmax.InvoiceNotFound(number),
            _ => Errors.Revmax.DeviceUnavailable(number, $"Code {response.Code}: {response.Message}")
        };

    internal static bool IsNotFound(InvoiceResponse response) =>
        response.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;
}
