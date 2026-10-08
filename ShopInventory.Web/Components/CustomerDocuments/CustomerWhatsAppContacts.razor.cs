using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Features.CustomerDocuments.Commands.OptOutCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Commands.RecheckCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Commands.RemoveCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Commands.SaveCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Commands.UpdateCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContacts;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.CustomerDocuments;

/// <summary>
/// The register of a customer's WhatsApp numbers, for one owner: an account card or a route customer.
/// </summary>
/// <remarks>
/// Consent is asked for every time a number is added, because the API refuses one without it and the
/// person adding it is the witness the record names. Opting out and removing are confirmed inline,
/// because one stops every document to that phone and the other cannot be undone from here.
/// </remarks>
public partial class CustomerWhatsAppContacts : ComponentBase, IDisposable
{
    private enum ContactAction
    {
        OptOut,
        Remove
    }

    private readonly CancellationTokenSource disposal = new();
    private readonly HashSet<string> chosenSiblings = new(StringComparer.OrdinalIgnoreCase);

    private List<CustomerWhatsAppContactModel> contacts = [];
    private string? loadedOwner;
    private bool isLoading;
    private string? message;
    private bool messageIsError;
    private int? busyContactId;

    private bool isAdding;
    private bool isSaving;
    private string? newPhone;
    private string? newContactName;
    private bool newConsent;
    private string? newConsentNote;
    private bool newAutoSend = true;

    private CustomerWhatsAppContactModel? confirming;
    private ContactAction confirmAction;

    [Inject] private IMediator Mediator { get; set; } = default!;

    /// <summary>The account customer's SAP card. Give this or <see cref="RouteCustomerId"/>.</summary>
    [Parameter] public string? CardCode { get; set; }

    /// <summary>The van route customer.</summary>
    [Parameter] public int? RouteCustomerId { get; set; }

    /// <summary>The customer's name, sent only for when SAP cannot be asked.</summary>
    [Parameter] public string? OwnerName { get; set; }

    /// <summary>Numbers SAP already holds for the customer, offered to fill the field — never saved without consent.</summary>
    [Parameter] public IReadOnlyList<string>? SuggestedPhones { get; set; }

    /// <summary>The same shop's cards in its other currencies, offered unticked.</summary>
    [Parameter] public IReadOnlyList<SiblingCard>? SiblingCards { get; set; }

    private string OwnerKey => CardCode is not null ? $"card:{CardCode}" : $"route:{RouteCustomerId}";

    private string IdSuffix => (CardCode ?? RouteCustomerId?.ToString() ?? "x").Replace(' ', '-');

    protected override async Task OnParametersSetAsync()
    {
        if (OwnerKey != loadedOwner)
        {
            loadedOwner = OwnerKey;
            contacts = [];
            isAdding = false;
            message = null;
            confirming = null;
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(CardCode) && RouteCustomerId is null)
        {
            return;
        }

        isLoading = true;
        try
        {
            var result = await Mediator.Send(new GetCustomerWhatsAppContactsQuery(CardCode, RouteCustomerId), disposal.Token);
            if (result.IsError)
            {
                Show(result.FirstError.Description, isError: true);
            }
            else
            {
                contacts = result.Value;
            }
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isLoading = false;
        }
    }

    private void BeginAdd()
    {
        isAdding = true;
        message = null;
        newPhone = null;
        newContactName = null;
        newConsent = false;
        newConsentNote = null;
        newAutoSend = true;
        chosenSiblings.Clear();
    }

    private void CancelAdd() => isAdding = false;

    private void ToggleSibling(string cardCode, bool chosen)
    {
        if (chosen)
        {
            chosenSiblings.Add(cardCode);
        }
        else
        {
            chosenSiblings.Remove(cardCode);
        }
    }

    private async Task SaveAsync()
    {
        isSaving = true;
        message = null;

        try
        {
            var result = await Mediator.Send(new SaveCustomerWhatsAppContactCommand(new SaveCustomerWhatsAppContactModel
            {
                CardCode = CardCode,
                RouteCustomerId = CardCode is null ? RouteCustomerId : null,
                Phone = newPhone?.Trim() ?? string.Empty,
                ContactName = string.IsNullOrWhiteSpace(newContactName) ? null : newContactName.Trim(),
                AutoSendInvoices = newAutoSend,
                ConsentConfirmed = newConsent,
                ConsentNote = string.IsNullOrWhiteSpace(newConsentNote) ? null : newConsentNote.Trim(),
                AlsoApplyToCardCodes = CardCode is not null && chosenSiblings.Count > 0 ? chosenSiblings.ToList() : null,
                OwnerName = OwnerName
            }), disposal.Token);

            if (result.IsError)
            {
                Show(result.FirstError.Description, isError: true);
                return;
            }

            isAdding = false;
            var saved = result.Value;
            Show(saved.Count == 1
                ? $"Saved {saved[0].PhoneE164}."
                : $"Saved {saved[0].PhoneE164} on {saved.Count} accounts.", isError: false);
            await LoadAsync();
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task ToggleAutoSendAsync(CustomerWhatsAppContactModel contact)
    {
        await RunOnContactAsync(contact, async () =>
        {
            var result = await Mediator.Send(new UpdateCustomerWhatsAppContactCommand(
                contact.Id,
                new UpdateCustomerWhatsAppContactModel
                {
                    ContactName = contact.ContactName,
                    AutoSendInvoices = !contact.AutoSendInvoices
                }), disposal.Token);

            return result.IsError
                ? (result.FirstError.Description, true)
                : (result.Value.AutoSendInvoices
                    ? $"New invoices will go to {contact.PhoneE164} automatically."
                    : $"Invoices go to {contact.PhoneE164} only when someone sends one.", false);
        });
    }

    private async Task RecheckAsync(CustomerWhatsAppContactModel contact)
    {
        await RunOnContactAsync(contact, async () =>
        {
            var result = await Mediator.Send(new RecheckCustomerWhatsAppContactCommand(contact.Id), disposal.Token);
            return result.IsError
                ? (result.FirstError.Description, true)
                : (result.Value.WhatsAppExists == true
                    ? $"{contact.PhoneE164} is on WhatsApp."
                    : $"WhatsApp has no account for {contact.PhoneE164}.", result.Value.WhatsAppExists != true);
        });
    }

    private void BeginConfirm(CustomerWhatsAppContactModel contact, ContactAction action)
    {
        confirming = contact;
        confirmAction = action;
    }

    private void CancelConfirm() => confirming = null;

    private async Task ConfirmAsync()
    {
        if (confirming is not { } contact)
        {
            return;
        }

        await RunOnContactAsync(contact, async () =>
        {
            if (confirmAction == ContactAction.OptOut)
            {
                var optOut = await Mediator.Send(new OptOutCustomerWhatsAppContactCommand(contact.Id), disposal.Token);
                return optOut.IsError
                    ? (optOut.FirstError.Description, true)
                    : ($"{contact.PhoneE164} will receive no more documents, on any customer it is saved on.", false);
            }

            var removed = await Mediator.Send(new RemoveCustomerWhatsAppContactCommand(contact.Id), disposal.Token);
            return removed.IsError
                ? (removed.FirstError.Description, true)
                : ($"{contact.PhoneE164} was taken off the customer.", false);
        });

        confirming = null;
    }

    private async Task RunOnContactAsync(
        CustomerWhatsAppContactModel contact,
        Func<Task<(string Message, bool IsError)>> action)
    {
        busyContactId = contact.Id;
        message = null;

        try
        {
            var (text, isError) = await action();
            Show(text, isError);
            await LoadAsync();
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            busyContactId = null;
        }
    }

    private void Show(string text, bool isError)
    {
        message = text;
        messageIsError = isError;
    }

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
