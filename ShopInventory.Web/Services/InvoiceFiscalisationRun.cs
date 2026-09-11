using System.Net;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Fiscalises invoices from the invoice list — one, or a selection of them one after another.
/// </summary>
/// <remarks>
/// Outside the page so the two rules that make a bulk run safe can be tested without rendering it:
/// what a thrown call means, and when a run has to stop. A fiscal receipt cannot be withdrawn, so both
/// are decided the same way <see cref="FiscalRetryResult"/> decides them for the console.
/// </remarks>
public static class InvoiceFiscalisationRun
{
    /// <summary>
    /// Fiscalises one invoice and classifies what happened to it.
    /// </summary>
    public static async Task<FiscalRetryResult> FiscaliseAsync(IInvoiceService invoiceService, InvoiceDto invoice)
    {
        try
        {
            var result = await invoiceService.FiscalizeInvoiceAsync(invoice.DocEntry);
            return FiscalRetryResult.From(invoice.DocNum, result);
        }
        catch (HttpRequestException ex) when (WasRefusedBeforeSubmission(ex.StatusCode))
        {
            // Every refusal the fiscalise route returns — not found, not posted, a consolidated invoice,
            // a sale already fiscalised before SAP — is raised before the fiscal service is called.
            return FiscalRetryResult.Blocked(ApiErrorResponse.GetFriendlyMessage(
                ex, $"Invoice #{invoice.DocNum} was refused, so nothing was submitted."));
        }
        catch (Exception ex)
        {
            // A 5xx, a timeout or a dropped connection can all arrive after the receipt reached FDMS.
            return FiscalRetryResult.Unknown(invoice.DocNum, ApiErrorResponse.GetFriendlyMessage(
                ex, "The server gave no answer that says whether the invoice reached FDMS."));
        }
    }

    /// <summary>
    /// Fiscalises the invoices in order, one at a time.
    /// </summary>
    /// <param name="invoiceService">The route each invoice is sent through.</param>
    /// <param name="invoices">What to fiscalise, in the order to send it.</param>
    /// <param name="stopRequested">Read between invoices, never during one: a submission is not interrupted.</param>
    /// <param name="onStarting">Called before each invoice is sent.</param>
    /// <param name="onFinished">Called with each invoice's outcome, before the next is sent.</param>
    /// <remarks>
    /// Stops at the first outcome nobody could establish. Whatever made that one indeterminate — the
    /// platform timing out, the connection dropping — is likely to do the same to the next, and every
    /// invoice sent into it becomes another receipt someone has to look up by hand.
    /// </remarks>
    public static async Task<InvoiceFiscalisationSummary> RunAsync(
        IInvoiceService invoiceService,
        IReadOnlyList<InvoiceDto> invoices,
        Func<bool> stopRequested,
        Func<InvoiceDto, Task>? onStarting = null,
        Func<InvoiceDto, FiscalRetryResult, Task>? onFinished = null)
    {
        var outcomes = new List<InvoiceFiscalisationOutcome>(invoices.Count);
        var stoppedByUser = false;

        foreach (var invoice in invoices)
        {
            if (stopRequested())
            {
                stoppedByUser = true;
                break;
            }

            if (onStarting is not null)
            {
                await onStarting(invoice);
            }

            var result = await FiscaliseAsync(invoiceService, invoice);
            outcomes.Add(new InvoiceFiscalisationOutcome(invoice, result));

            if (onFinished is not null)
            {
                await onFinished(invoice, result);
            }

            if (result.MustNotRetry)
            {
                break;
            }
        }

        return new InvoiceFiscalisationSummary(outcomes, invoices.Count, stoppedByUser);
    }

    // 408 is left out: a proxy can answer it after the request has already been handed on.
    private static bool WasRefusedBeforeSubmission(HttpStatusCode? statusCode) =>
        statusCode is { } code
        && (int)code is >= 400 and < 500
        && code != HttpStatusCode.RequestTimeout;
}

public sealed record InvoiceFiscalisationOutcome(InvoiceDto Invoice, FiscalRetryResult Result);

/// <summary>
/// What a run did, in the terms the person who started it has to act on.
/// </summary>
public sealed class InvoiceFiscalisationSummary(
    IReadOnlyList<InvoiceFiscalisationOutcome> outcomes,
    int requested,
    bool stoppedByUser)
{
    public IReadOnlyList<InvoiceFiscalisationOutcome> Outcomes { get; } = outcomes;

    public int Requested { get; } = requested;

    /// <summary>The run was stopped by the person who started it, between two invoices.</summary>
    public bool StoppedByUser { get; } = stoppedByUser;

    public int Fiscalised => Count(FiscalRetryOutcome.Fiscalised);

    public int AlreadyFiscalised => Count(FiscalRetryOutcome.AlreadyFiscalised);

    /// <summary>Not fiscalised, with nothing recorded at FDMS. Safe to send again.</summary>
    public IReadOnlyList<InvoiceFiscalisationOutcome> NotFiscalised =>
        Outcomes.Where(outcome => !outcome.Result.IsSettled && !outcome.Result.MustNotRetry).ToList();

    /// <summary>The outcome that ended the run early, if one did. It must be looked up, not sent again.</summary>
    public InvoiceFiscalisationOutcome? Unresolved =>
        Outcomes.FirstOrDefault(outcome => outcome.Result.MustNotRetry);

    /// <summary>Never sent, because the run stopped before reaching them.</summary>
    public int NotSent => Requested - Outcomes.Count;

    private int Count(FiscalRetryOutcome outcome) =>
        Outcomes.Count(item => item.Result.Outcome == outcome);
}
