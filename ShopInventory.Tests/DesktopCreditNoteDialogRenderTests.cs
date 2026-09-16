using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Components;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The credit-note dialog renders, against stubbed services.
/// </summary>
/// <remarks>
/// <para>
/// This markup lived inside <c>DesktopSales.razor</c>, which is gated to roles that vending and van
/// staff do not hold — so a return handed back at a cart or a shop had nowhere to be credited. Moving
/// it into a component is what let the vending list host the same dialog, and a 280-line move out of a
/// 2,700-line page is exactly the change a clean build says nothing useful about: a Razor page can
/// compile and still render nothing, or render without the handlers it used to have.
/// </para>
/// <para>
/// So these render it for real, through the component's own lifecycle, and read the resulting HTML.
/// Not a substitute for driving the running app — they cannot press a button — but they prove the
/// component mounts, reads its services, and puts the operator's controls on the page.
/// </para>
/// </remarks>
public sealed class DesktopCreditNoteDialogRenderTests
{
    private const string Reference = "VERIFY-VEND-0001";

    [Fact]
    public async Task It_renders_the_receipt_the_lines_and_both_actions()
    {
        var html = await RenderAsync(Form(saleInSap: true, sapDocNum: 48213));

        // The sale it is crediting, and the receipt it reverses.
        Assert.Contains(Reference, html);
        Assert.Contains("Receipt", html);
        Assert.Contains("In SAP", html);
        Assert.Contains("invoice #48213", html);

        // The line the operator picks a quantity on, with its stepper.
        Assert.Contains("Cheddar 1kg", html);
        Assert.Contains("Credit quantity for Cheddar 1kg", html);
        Assert.Contains("Credit everything", html);

        // Both actions. "Fiscalise and post to SAP" is the one that only appears on a sale in SAP.
        Assert.Contains("Fiscalise only", html);
        Assert.Contains("Fiscalise and post to SAP", html);

        // The reasons came from ICreditNoteService, so the select is rendered rather than the free-text
        // fallback that exists only for when that list cannot be read.
        Assert.Contains("Damaged in transit", html);
        Assert.DoesNotContain("Why is this being credited?", html);
    }

    [Fact]
    public async Task A_sale_not_yet_in_SAP_cannot_be_posted_to_SAP()
    {
        var html = await RenderAsync(Form(saleInSap: false, sapDocNum: null));

        Assert.Contains("Not yet in SAP", html);

        // The button is still drawn — the operator has to be able to see what is unavailable — but it
        // is disabled, and stays disabled however the lines are filled in, because the sale has no
        // invoice for a memo to be raised against.
        Assert.Contains("Fiscalise and post to SAP", html);
        Assert.Contains("disabled", html);

        // With nothing picked yet, the footer says what is missing rather than what the buttons do.
        // Which of the two it says is the whole point of the hint, so it is read rather than assumed.
        Assert.Contains("Enter a credit quantity on at least one line to continue", html);
    }

    [Fact]
    public async Task A_receipt_already_fully_credited_offers_no_quantities_and_says_why()
    {
        var html = await RenderAsync(Form(saleInSap: true, sapDocNum: 48213, remaining: 0m));

        Assert.Contains("already been fully credited with ZIMRA", html);
    }

    [Fact]
    public async Task Nothing_is_rendered_without_a_sale()
    {
        // How a host closes it: the reference goes null and the overlay has to go with it.
        var html = await RenderAsync(Form(saleInSap: true, sapDocNum: 1), reference: null);

        Assert.DoesNotContain("ops-overlay", html);
    }

    [Fact]
    public async Task A_refusal_from_the_API_is_shown_rather_than_thrown()
    {
        // What a van sale posted through its reservation gets, and what the platform provider gets.
        // The dialog has to put the refusal in front of the operator, not disappear on it.
        var html = await RenderAsync(form: null,
            prepareError: "This sale reached SAP through its reservation, so raise the credit note against "
                + "the SAP invoice instead, from Credit notes.");

        Assert.Contains("raise the credit note against the SAP invoice instead", html);
        Assert.Contains("ops-dcn-alert", html);
    }

    // ---------------------------------------------------------------

    private static DesktopCreditForm Form(bool saleInSap, int? sapDocNum, decimal remaining = 23m) => new(
        new DesktopCreditSource(
            OriginalFiscalNumber: Reference,
            Currency: "USD",
            OriginalTotal: 23m,
            DeviceId: 22862,
            FiscalDayNo: 531,
            ReceiptGlobalNo: 90210,
            ReceiptId: null,
            Lines: [new DesktopCreditLine(1, "Cheddar 1kg", 2m, 11.50m, 7, 15.5m, "O01", "04069000")]),
        CreditNotes: [],
        ReservedQuantities: [],
        SaleInSap: saleInSap,
        SaleSapDocNum: sapDocNum,
        RemainingAmount: remaining);

    private static async Task<string> RenderAsync(
        DesktopCreditForm? form,
        string? reference = Reference,
        string? prepareError = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Desktop(form, prepareError));
        services.AddSingleton(Reasons());

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<DesktopCreditNoteDialog>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(DesktopCreditNoteDialog.Reference)] = reference,
                    [nameof(DesktopCreditNoteDialog.Currency)] = "USD",
                    [nameof(DesktopCreditNoteDialog.IsConsolidated)] = false
                }));

            return output.ToHtmlString();
        });
    }

    /// <summary>
    /// Answers the two reads the dialog makes when it opens, and nothing else.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="StubProxy"/> because <see cref="IDesktopIntegrationService"/> has dozens of
    /// members. Anything the dialog calls beyond these two throws, so a change in what opening it does
    /// fails here rather than passing quietly on a default.
    /// </remarks>
    private static IDesktopIntegrationService Desktop(DesktopCreditForm? form, string? prepareError) =>
        StubProxy.For<IDesktopIntegrationService>((method, _) => method.Name switch
        {
            nameof(IDesktopIntegrationService.GetCreditNotesAsync) =>
                (object)Task.FromResult(new List<DesktopCreditNoteResult>()),

            nameof(IDesktopIntegrationService.PrepareCreditNoteAsync) => prepareError is not null
                ? Task.FromException<DesktopCreditForm>(new InvalidOperationException(prepareError))
                : Task.FromResult(form!),

            _ => throw new InvalidOperationException(
                $"IDesktopIntegrationService.{method.Name} was not expected while opening the dialog.")
        });

    private static ICreditNoteService Reasons() =>
        StubProxy.For<ICreditNoteService>((method, _) =>
            method.Name == nameof(ICreditNoteService.GetReasonsAsync)
                ? Task.FromResult<IReadOnlyList<CreditNoteReasonOption>>(
                    [new CreditNoteReasonOption { Value = "DMG", Description = "Damaged in transit" }])
                : throw new InvalidOperationException(
                    $"ICreditNoteService.{method.Name} was not expected while opening the dialog."));
}
