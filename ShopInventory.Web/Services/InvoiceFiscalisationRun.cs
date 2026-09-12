using System.Net;
using MudBlazor;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Fiscalises a selection of documents, one after another, under the rules that make a bulk run safe.
/// </summary>
/// <remarks>
/// Generic over what is being sent because two pages send different things through it: the invoice list
/// sends <see cref="InvoiceDto"/> down the invoice route, and the fiscalisation console sends work-queue
/// rows through its own DocNum lookup. Only the send differs. When to stop does not, and a fiscal
/// receipt cannot be withdrawn — so that rule is written once, here, rather than twice in two pages.
/// </remarks>
public static class FiscalisationRun
{
    /// <summary>
    /// Sends each item in order, one at a time.
    /// </summary>
    /// <param name="items">What to fiscalise, in the order to send it.</param>
    /// <param name="fiscalise">Sends one item and classifies what happened to it. Must not throw.</param>
    /// <param name="stopRequested">Read between items, never during one: a submission is not interrupted.</param>
    /// <param name="onStarting">Called before each item is sent.</param>
    /// <param name="onFinished">Called with each item's outcome, before the next is sent.</param>
    /// <remarks>
    /// Stops at the first outcome nobody could establish. Whatever made that one indeterminate — the
    /// platform timing out, the connection dropping — is likely to do the same to the next, and every
    /// document sent into it becomes another receipt someone has to look up by hand.
    /// </remarks>
    public static async Task<FiscalisationRunSummary<T>> RunAsync<T>(
        IReadOnlyList<T> items,
        Func<T, Task<FiscalRetryResult>> fiscalise,
        Func<bool> stopRequested,
        Func<T, Task>? onStarting = null,
        Func<T, FiscalRetryResult, Task>? onFinished = null)
    {
        var outcomes = new List<FiscalisationOutcome<T>>(items.Count);
        var stoppedByUser = false;

        foreach (var item in items)
        {
            if (stopRequested())
            {
                stoppedByUser = true;
                break;
            }

            if (onStarting is not null)
            {
                await onStarting(item);
            }

            var result = await fiscalise(item);
            outcomes.Add(new FiscalisationOutcome<T>(item, result));

            if (onFinished is not null)
            {
                await onFinished(item, result);
            }

            if (result.MustNotRetry)
            {
                break;
            }
        }

        return new FiscalisationRunSummary<T>(outcomes, items.Count, stoppedByUser);
    }
}

/// <summary>
/// Fiscalises invoices from the invoice list — one, or a selection of them one after another.
/// </summary>
/// <remarks>
/// Outside the page so the two rules that make a bulk run safe can be tested without rendering it:
/// what a thrown call means, and when a run has to stop. A fiscal receipt cannot be withdrawn, so both
/// are decided the same way <see cref="FiscalRetryResult"/> decides them for the console. The second of
/// those rules now lives in <see cref="FiscalisationRun"/>, which the console shares.
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
    public static Task<FiscalisationRunSummary<InvoiceDto>> RunAsync(
        IInvoiceService invoiceService,
        IReadOnlyList<InvoiceDto> invoices,
        Func<bool> stopRequested,
        Func<InvoiceDto, Task>? onStarting = null,
        Func<InvoiceDto, FiscalRetryResult, Task>? onFinished = null) =>
        FiscalisationRun.RunAsync(
            invoices,
            invoice => FiscaliseAsync(invoiceService, invoice),
            stopRequested,
            onStarting,
            onFinished);

    // 408 is left out: a proxy can answer it after the request has already been handed on.
    private static bool WasRefusedBeforeSubmission(HttpStatusCode? statusCode) =>
        statusCode is { } code
        && (int)code is >= 400 and < 500
        && code != HttpStatusCode.RequestTimeout;
}

public sealed record FiscalisationOutcome<T>(T Item, FiscalRetryResult Result);

/// <summary>
/// What a run did, in the terms the person who started it has to act on.
/// </summary>
public sealed class FiscalisationRunSummary<T>(
    IReadOnlyList<FiscalisationOutcome<T>> outcomes,
    int requested,
    bool stoppedByUser)
{
    public IReadOnlyList<FiscalisationOutcome<T>> Outcomes { get; } = outcomes;

    public int Requested { get; } = requested;

    /// <summary>The run was stopped by the person who started it, between two documents.</summary>
    public bool StoppedByUser { get; } = stoppedByUser;

    public int Fiscalised => Count(FiscalRetryOutcome.Fiscalised);

    public int AlreadyFiscalised => Count(FiscalRetryOutcome.AlreadyFiscalised);

    /// <summary>Not fiscalised, with nothing recorded at FDMS. Safe to send again.</summary>
    public IReadOnlyList<FiscalisationOutcome<T>> NotFiscalised =>
        Outcomes.Where(outcome => !outcome.Result.IsSettled && !outcome.Result.MustNotRetry).ToList();

    /// <summary>The outcome that ended the run early, if one did. It must be looked up, not sent again.</summary>
    public FiscalisationOutcome<T>? Unresolved =>
        Outcomes.FirstOrDefault(outcome => outcome.Result.MustNotRetry);

    /// <summary>Never sent, because the run stopped before reaching them.</summary>
    public int NotSent => Requested - Outcomes.Count;

    private int Count(FiscalRetryOutcome outcome) =>
        Outcomes.Count(item => item.Result.Outcome == outcome);
}

/// <summary>
/// Says what a bulk run did, in a toast.
/// </summary>
/// <remarks>
/// Shared by the invoice list and the fiscalisation console because the rule that matters here is not
/// wording, it is that an unresolved outcome gets its own error toast that will not dismiss itself. A
/// run that stopped because nobody could establish what happened to a document has left a receipt that
/// may or may not exist at FDMS, and a toast that fades after four seconds is not how someone finds out.
/// </remarks>
public static class FiscalisationRunReport
{
    /// <param name="snackbar">Where the toasts go.</param>
    /// <param name="summary">What the run did.</param>
    /// <param name="label">How one item is named in the summary, e.g. <c>#772575</c>.</param>
    /// <param name="singular">The noun for one item — <c>invoice</c>, <c>document</c>.</param>
    /// <param name="plural">Its plural.</param>
    public static void Announce<T>(
        ISnackbar snackbar,
        FiscalisationRunSummary<T> summary,
        Func<T, string> label,
        string singular,
        string plural)
    {
        var parts = new List<string>
        {
            summary.Fiscalised == summary.Requested
                ? $"Fiscalised {Count(summary.Requested)}."
                : $"Fiscalised {summary.Fiscalised:N0} of {Count(summary.Requested)}."
        };

        if (summary.AlreadyFiscalised > 0)
        {
            parts.Add($"{summary.AlreadyFiscalised:N0} already fiscalised.");
        }

        var notFiscalised = summary.NotFiscalised;
        var reasons = notFiscalised.Select(item => item.Result.Message).Distinct().ToList();
        if (notFiscalised.Count > 0)
        {
            var names = string.Join(", ", notFiscalised.Take(5).Select(item => label(item.Item)))
                        + (notFiscalised.Count > 5 ? ", …" : string.Empty);

            parts.Add(reasons.Count == 1
                ? $"{names} not fiscalised: {reasons[0]}"
                : $"{names} not fiscalised, and still selected.");
        }

        if (summary.StoppedByUser && summary.NotSent > 0)
        {
            parts.Add($"Stopped with {Count(summary.NotSent)} not sent.");
        }

        var allSettled = summary.Fiscalised + summary.AlreadyFiscalised == summary.Requested;
        snackbar.Add(
            string.Join(" ", parts),
            allSettled ? Severity.Success : Severity.Warning,
            allSettled ? null : config => config.RequireInteraction = true);

        if (summary.Unresolved is { } unresolved)
        {
            var notSent = summary.NotSent > 0 ? $" The run stopped there: {Count(summary.NotSent)} not sent." : string.Empty;
            snackbar.Add(
                WithDetail(unresolved.Result) + notSent,
                Severity.Error,
                config => config.RequireInteraction = true);
        }

        string Count(int count) => count == 1 ? $"1 {singular}" : $"{count:N0} {plural}";
    }

    /// <summary>
    /// The platform's own wording appended to ours, unless ours already carries it.
    /// </summary>
    public static string WithDetail(FiscalRetryResult result)
        => string.IsNullOrWhiteSpace(result.Detail) || result.Message.Contains(result.Detail, StringComparison.Ordinal)
            ? result.Message
            : $"{result.Message} ({result.Detail.TrimEnd('.')})";
}
