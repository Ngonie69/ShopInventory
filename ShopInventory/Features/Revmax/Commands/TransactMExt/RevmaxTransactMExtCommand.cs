using ErrorOr;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Features.Revmax.Queries.GetRevmaxInvoice;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Commands.TransactMExt;

/// <summary>Sends one request to REVMax's <c>TransactMExt</c>, which files a receipt with ZIMRA.</summary>
public sealed record RevmaxTransactMExtCommand(TransactMExtRequest Request, string? RequestedBy)
    : IRequest<ErrorOr<TransactMExtResponse>>;

public sealed class RevmaxTransactMExtValidator : AbstractValidator<RevmaxTransactMExtCommand>
{
    public RevmaxTransactMExtValidator()
    {
        RuleFor(x => x.Request).NotNull().WithMessage("A TransactMExt body is required");
        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.InvoiceNumber).NotEmpty().MaximumLength(100)
                .WithMessage("InvoiceNumber is required");
            RuleFor(x => x.Request.Currency).NotEmpty().WithMessage("Currency is required");
            RuleFor(x => x.Request.Istatus).NotEmpty().WithMessage("Istatus is required (02 for a credit note)");
            RuleFor(x => x.Request.ItemsXml).NotNull().WithMessage("ItemsXml is required");
            RuleFor(x => x.Request.CurrenciesXml).NotNull().WithMessage("CurrenciesXml is required");
            RuleFor(x => x.Request.refDeviceId).NotNull().GreaterThan(0)
                .WithMessage("refDeviceId is required: the device that filed the receipt being credited");
            RuleFor(x => x.Request.refReceiptGlobalNo).NotNull().GreaterThan(0)
                .WithMessage("refReceiptGlobalNo is required: the global number of the receipt being credited");
            RuleFor(x => x.Request.refFiscalDayNo).NotNull().GreaterThan(0)
                .WithMessage("refFiscalDayNo is required: the fiscal day of the receipt being credited");
        });
    }
}

/// <remarks>
/// Filing is irreversible, so three things happen before anything is sent:
/// <list type="bullet">
/// <item>REVMax must still be enabled here.</item>
/// <item>The reference must be another device's receipt. Crediting REVMax's own receipt through
/// TransactMExt files it as reversing some other device's receipt; see
/// <see cref="RevmaxFiscalizationService"/>.</item>
/// <item>GetInvoice must say the number is free. A number REVMax already holds is refused, and so is a
/// number it cannot vouch for either way.</item>
/// </list>
/// Then one POST, never retried (<see cref="RevmaxClient"/>). A timeout or error leaves the outcome
/// unknown, and the caller is told to look it up rather than resend.
/// </remarks>
public sealed class RevmaxTransactMExtHandler(
    IRevmaxClient client,
    IOptions<RevmaxSettings> settings,
    ILogger<RevmaxTransactMExtHandler> logger
) : IRequestHandler<RevmaxTransactMExtCommand, ErrorOr<TransactMExtResponse>>
{
    public async Task<ErrorOr<TransactMExtResponse>> Handle(
        RevmaxTransactMExtCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Revmax.Disabled;

        var request = command.Request;
        var number = request.InvoiceNumber!.Trim();
        request.InvoiceNumber = number;

        if (request.refDeviceId == settings.Value.DefaultRefDeviceId)
            return Errors.Revmax.OwnDeviceReference(request.refDeviceId!.Value);

        InvoiceResponse? existing;
        try
        {
            existing = await client.GetInvoiceAsync(number, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "REVMax GetInvoice before TransactMExt {InvoiceNumber} failed; nothing sent", number);
            return Errors.Revmax.DeviceUnavailable(number, ex.Message);
        }

        var lookup = GetRevmaxInvoiceHandler.Classify(number, existing);
        if (!lookup.IsError)
            return Errors.Revmax.AlreadyFiled(number, lookup.Value.Data?.ReceiptGlobalNo ?? 0, lookup.Value.Data?.ReceiptType);
        if (lookup.FirstError.Type != ErrorType.NotFound)
            return lookup.Errors;

        logger.LogWarning(
            "REVMax TransactMExt {InvoiceNumber} sent by {User}: {Currency} {Amount}, crediting device {RefDevice} receipt {RefGlobal} day {RefDay}",
            number, command.RequestedBy ?? "(unknown)", request.Currency, request.InvoiceAmount,
            request.refDeviceId, request.refReceiptGlobalNo, request.refFiscalDayNo);

        TransactMExtResponse? response;
        try
        {
            response = await client.TransactMExtAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "REVMax TransactMExt {InvoiceNumber} has an unknown outcome", number);
            return Errors.Revmax.OutcomeUnknown(number, ex.Message);
        }

        if (response is null)
        {
            logger.LogError("REVMax TransactMExt {InvoiceNumber} returned an empty body", number);
            return Errors.Revmax.OutcomeUnknown(number, "the device returned an empty body");
        }

        logger.LogWarning(
            "REVMax TransactMExt {InvoiceNumber} answered Code {Code}: {Message}; receipt {Global}",
            number, response.Code, response.Message, response.ReceiptGlobalNo);

        // The device refuses with HTTP 200 and Code "0", so a refusal must not reach the caller as a 200.
        return response.Success
            ? response
            : Errors.Revmax.Refused(number, response.Code, response.Message);
    }
}
