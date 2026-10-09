using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Features.CustomerDocuments.Commands.CancelCustomerDocumentDelivery;
using ShopInventory.Web.Features.CustomerDocuments.Commands.RequestInvoiceWhatsApp;
using ShopInventory.Web.Features.CustomerDocuments.Commands.RetryCustomerDocumentDelivery;
using ShopInventory.Web.Features.CustomerDocuments.Queries.CheckWhatsAppNumber;
using ShopInventory.Web.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContacts;
using ShopInventory.Web.Features.CustomerDocuments.Queries.GetInvoiceWhatsAppDeliveries;
using ShopInventory.Web.Features.CustomerDocuments.Queries.PreviewInvoiceWhatsApp;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.CustomerDocuments;

/// <summary>
/// One invoice's WhatsApp sends, and the dialog that queues another.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here sends. Pressing Send writes delivery rows through the API, and the API's delivery job
/// sends them — after confirming the fiscal receipt, at the pace that keeps the number in good
/// standing. So the history is followed for a few minutes after a send, until each row has settled,
/// rather than the button claiming a delivery it cannot see.
/// </para>
/// <para>
/// Whether a number may be used, whether the invoice may go, and how many one-off numbers a person
/// may use are the API's decisions; this shows its sentence when it refuses.
/// </para>
/// </remarks>
public partial class InvoiceWhatsAppPanel : ComponentBase, IDisposable
{
    private static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(8);
    private const int MaxFollowTicks = 30;

    private readonly CancellationTokenSource disposal = new();

    private List<CustomerDocumentDeliveryModel> deliveries = [];
    private int loadedDocEntry;
    private bool isLoading;
    private string? loadError;
    private long? busyDeliveryId;
    private bool following;

    private bool isSendOpen;
    private bool isLoadingContacts;
    private List<CustomerWhatsAppContactModel> contacts = [];
    private readonly HashSet<int> chosenContacts = [];
    private string? oneOffPhone;
    private string? oneOffName;
    private bool consentAffirmed;
    private bool saveAsContact;
    private bool autoSendFuture;
    private bool isChecking;
    private WhatsAppNumberCheckResultModel? oneOffCheck;
    private bool isPreviewing;
    private InvoiceWhatsAppPreviewModel? preview;
    private bool isSending;
    private string? sendError;

    private CustomerDocumentDeliveryModel? retrying;
    private bool retryConfirmed;
    private bool isRetrying;
    private string? retryError;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    /// <summary>The SAP invoice.</summary>
    [Parameter, EditorRequired] public int DocEntry { get; set; }

    [Parameter] public int DocNum { get; set; }

    [Parameter] public string? CardCode { get; set; }

    [Parameter] public string? CardName { get; set; }

    /// <summary>
    /// The shop a van invoice was for. A van bills every shop to its own card, so for a van invoice the
    /// numbers offered, checked and saved are the shop's, never the card's.
    /// </summary>
    [Parameter] public int? RouteCustomerId { get; set; }

    private bool HasOneOff => !string.IsNullOrWhiteSpace(oneOffPhone);

    // A typed number holds the whole send back until its consent is ticked, even with saved numbers
    // chosen beside it: the API refuses the request as a whole, and the saved numbers would not go.
    private bool CanSend => (chosenContacts.Count > 0 || HasOneOff) && (!HasOneOff || consentAffirmed);

    private string SendLabel
    {
        get
        {
            var count = chosenContacts.Count + (HasOneOff ? 1 : 0);
            return count <= 1 ? "Send" : $"Send to {count} numbers";
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (DocEntry > 0 && DocEntry != loadedDocEntry)
        {
            loadedDocEntry = DocEntry;
            deliveries = [];
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        isLoading = true;
        loadError = null;

        try
        {
            var result = await Mediator.Send(new GetInvoiceWhatsAppDeliveriesQuery(DocEntry), disposal.Token);
            if (result.IsError)
            {
                loadError = result.FirstError.Description;
            }
            else
            {
                deliveries = result.Value;
            }
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            isLoading = false;
        }
    }

    // ── Sending ─────────────────────────────────────────────────────────

    private async Task OpenSendAsync()
    {
        isSendOpen = true;
        sendError = null;
        preview = null;
        oneOffCheck = null;
        oneOffPhone = null;
        oneOffName = null;
        consentAffirmed = false;
        saveAsContact = false;
        autoSendFuture = false;
        chosenContacts.Clear();
        contacts = [];

        if (RouteCustomerId is null && string.IsNullOrWhiteSpace(CardCode))
        {
            return;
        }

        isLoadingContacts = true;
        try
        {
            var result = await Mediator.Send(
                RouteCustomerId is { } shop
                    ? new GetCustomerWhatsAppContactsQuery(null, shop)
                    : new GetCustomerWhatsAppContactsQuery(CardCode, null),
                disposal.Token);
            if (result.IsError)
            {
                sendError = result.FirstError.Description;
                return;
            }

            contacts = result.Value;

            // The numbers the customer asked to receive their invoices on are the ones they would
            // expect this one on; the person can untick them.
            foreach (var contact in contacts.Where(c => c.AutoSendInvoices && !c.IsOptedOut && !c.IsRemoved && c.WhatsAppExists != false))
            {
                chosenContacts.Add(contact.Id);
            }
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            isLoadingContacts = false;
        }
    }

    private void CloseSend()
    {
        if (!isSending)
        {
            isSendOpen = false;
        }
    }

    private void ToggleContact(int contactId, bool chosen)
    {
        if (chosen)
        {
            chosenContacts.Add(contactId);
        }
        else
        {
            chosenContacts.Remove(contactId);
        }
    }

    private void ClearCheck() => oneOffCheck = null;

    private async Task CheckOneOffAsync()
    {
        if (!HasOneOff)
        {
            return;
        }

        isChecking = true;
        try
        {
            var result = await Mediator.Send(
                RouteCustomerId is { } shop
                    ? new CheckWhatsAppNumberQuery(oneOffPhone!.Trim(), null, shop)
                    : new CheckWhatsAppNumberQuery(oneOffPhone!.Trim(), CardCode, null),
                disposal.Token);
            oneOffCheck = result.IsError
                ? new WhatsAppNumberCheckResultModel { Input = oneOffPhone!, Message = result.FirstError.Description }
                : result.Value;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isChecking = false;
        }
    }

    private async Task PreviewAsync()
    {
        isPreviewing = true;
        preview = null;

        try
        {
            var result = await Mediator.Send(new PreviewInvoiceWhatsAppQuery(DocEntry), disposal.Token);
            preview = result.IsError
                ? new InvoiceWhatsAppPreviewModel { Ready = false, Reason = result.FirstError.Description }
                : result.Value;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isPreviewing = false;
        }
    }

    private async Task DownloadPreviewAsync()
    {
        if (preview is { Ready: true, PdfBase64: { Length: > 0 } pdf })
        {
            await JS.InvokeVoidAsync("downloadPdfFromBase64", pdf, preview.FileName ?? $"Kefalos-Invoice-{DocNum}.pdf");
        }
    }

    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        isSending = true;
        sendError = null;

        try
        {
            var request = new RequestInvoiceWhatsAppModel
            {
                ContactIds = chosenContacts.Count == 0 ? null : chosenContacts.ToList(),
                OneOffPhone = HasOneOff ? oneOffPhone!.Trim() : null,
                OneOffName = HasOneOff && !string.IsNullOrWhiteSpace(oneOffName) ? oneOffName.Trim() : null,
                ConsentAffirmed = HasOneOff && consentAffirmed,
                SaveAsContact = HasOneOff && saveAsContact,
                AutoSendFutureInvoices = HasOneOff && saveAsContact && autoSendFuture
            };

            var result = await Mediator.Send(new RequestInvoiceWhatsAppCommand(DocEntry, request), disposal.Token);
            if (result.IsError)
            {
                sendError = result.FirstError.Description;
                return;
            }

            isSendOpen = false;
            Snackbar.Add(
                result.Value.Count == 1
                    ? $"Invoice #{DocNum} is queued for {result.Value[0].RecipientMasked}. It goes once its fiscal receipt is confirmed."
                    : $"Invoice #{DocNum} is queued for {result.Value.Count} numbers. They go once its fiscal receipt is confirmed.",
                Severity.Success);

            await LoadAsync();
            _ = FollowAsync();
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isSending = false;
        }
    }

    // ── Resending and withdrawing ───────────────────────────────────────

    private void BeginRetry(CustomerDocumentDeliveryModel delivery)
    {
        retrying = delivery;
        retryConfirmed = false;
        retryError = null;
    }

    private void CloseRetry()
    {
        if (!isRetrying)
        {
            retrying = null;
        }
    }

    private async Task RetryAsync()
    {
        if (retrying is null)
        {
            return;
        }

        isRetrying = true;
        retryError = null;

        try
        {
            var result = await Mediator.Send(
                new RetryCustomerDocumentDeliveryCommand(retrying.Id, retrying.RetryNeedsConfirmation && retryConfirmed),
                disposal.Token);

            if (result.IsError)
            {
                retryError = result.FirstError.Description;
                return;
            }

            retrying = null;
            Snackbar.Add($"Invoice #{DocNum} is queued again for {result.Value.RecipientMasked}.", Severity.Success);
            await LoadAsync();
            _ = FollowAsync();
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isRetrying = false;
        }
    }

    private async Task CancelAsync(CustomerDocumentDeliveryModel delivery)
    {
        busyDeliveryId = delivery.Id;

        try
        {
            var result = await Mediator.Send(new CancelCustomerDocumentDeliveryCommand(delivery.Id), disposal.Token);
            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Warning);
            }
            else
            {
                Snackbar.Add("The send was withdrawn before it went.", Severity.Info);
            }

            await LoadAsync();
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            busyDeliveryId = null;
        }
    }

    /// <summary>
    /// Re-reads the history while a send is still on its way, so the person watching sees it settle
    /// without pressing refresh. Stops when nothing is moving, after a few minutes, or when the drawer
    /// closes.
    /// </summary>
    private async Task FollowAsync()
    {
        if (following)
        {
            return;
        }

        following = true;
        try
        {
            for (var tick = 0; tick < MaxFollowTicks && !disposal.IsCancellationRequested; tick++)
            {
                await Task.Delay(FollowInterval, disposal.Token);
                await InvokeAsync(async () =>
                {
                    await LoadAsync();
                    StateHasChanged();
                });

                if (!deliveries.Any(delivery => delivery.Status is "Pending" or "Preparing" or "Sending"))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The drawer closed between two reads.
        }
        finally
        {
            following = false;
        }
    }

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
