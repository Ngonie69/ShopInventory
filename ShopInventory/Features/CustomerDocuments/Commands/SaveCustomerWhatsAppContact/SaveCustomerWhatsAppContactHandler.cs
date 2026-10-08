using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.SaveCustomerWhatsAppContact;

/// <summary>
/// Saves a customer's WhatsApp number, on one card or on the same shop's cards in each currency.
/// </summary>
/// <remarks>
/// <para>
/// Consent is the gate: a number is saved only when the person saving it confirms the customer
/// agreed, and that person, the moment and their note are kept on the row. Saving a number the
/// customer once opted out of is allowed — it is fresh consent — and lifts that opt-out on this card.
/// </para>
/// <para>
/// A selling account is refused as an owner. A till, a van and a cart vendor invoice every buyer to
/// their own card, so a number on one would be sent every invoice that till, van or vendor raises.
/// </para>
/// <para>
/// The cards are checked against SAP in one read, and every contact is written in one save, so a
/// number copied across a shop's currency cards lands on all of them or none.
/// </para>
/// </remarks>
public sealed class SaveCustomerWhatsAppContactHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    IOptions<CustomerDocumentDeliverySettings> options,
    IAuditService auditService,
    ILogger<SaveCustomerWhatsAppContactHandler> logger)
    : IRequestHandler<SaveCustomerWhatsAppContactCommand, ErrorOr<List<CustomerWhatsAppContactDto>>>
{
    private static readonly Func<CustomerWhatsAppContactEntity, CustomerWhatsAppContactDto> ToDto =
        CustomerDocumentProjections.Contact.Compile();

    public async Task<ErrorOr<List<CustomerWhatsAppContactDto>>> Handle(
        SaveCustomerWhatsAppContactCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;
        var settings = options.Value;
        var cardCode = Clean(request.CardCode);

        if ((cardCode is null) == (request.RouteCustomerId is null))
            return Errors.CustomerDocuments.OwnerRequired;

        if (!request.ConsentConfirmed)
            return Errors.CustomerDocuments.ConsentRequired;

        if (!WhatsAppRecipients.TryNormalise(request.Phone, settings.DefaultCountryCode, out var phoneE164))
            return Errors.CustomerDocuments.InvalidPhone(request.Phone);

        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var owners = cardCode is not null
            ? await ResolveCardOwnersAsync(cardCode, request, cancellationToken)
            : await ResolveRouteCustomerOwnerAsync(request.RouteCustomerId!.Value, cancellationToken);

        if (owners.IsError)
            return owners.Errors;

        var now = DateTime.UtcNow;
        var saved = new List<CustomerWhatsAppContactEntity>();

        foreach (var owner in owners.Value)
        {
            var staged = await CustomerWhatsAppContactWriter.StageAsync(
                context,
                owner,
                phoneE164,
                request.ContactName,
                request.AutoSendInvoices,
                WhatsAppConsentSource.Web,
                request.ConsentNote,
                command.UserId,
                actorName,
                settings.MaxContactsPerOwner,
                now,
                cancellationToken);

            if (staged.IsError)
                return staged.Errors;

            saved.Add(staged.Value);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (CustomerDocumentDbErrors.IsDuplicate(ex, "PhoneE164"))
        {
            return Error.Conflict(
                "CustomerDocuments.ContactSavedConcurrently",
                "This number was saved on the customer by someone else a moment ago. Refresh and try again.");
        }

        foreach (var contact in saved)
        {
            await AuditAsync(contact);
        }

        return saved.Select(ToDto).ToList();
    }

    /// <summary>
    /// The card asked for and any sibling cards, each confirmed to be a customer SAP knows and not a
    /// selling account, named as SAP names it.
    /// </summary>
    private async Task<ErrorOr<List<ContactOwner>>> ResolveCardOwnersAsync(
        string cardCode,
        SaveCustomerWhatsAppContactRequest request,
        CancellationToken cancellationToken)
    {
        var codes = new List<string> { cardCode };
        foreach (var sibling in request.AlsoApplyToCardCodes ?? [])
        {
            var cleaned = Clean(sibling);
            if (cleaned is not null && !codes.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                codes.Add(cleaned);
        }

        foreach (var code in codes)
        {
            if (await SellingAccountCards.IsSellingAccountAsync(context, code, cancellationToken))
                return Errors.CustomerDocuments.SellingAccountNotAllowed(code);
        }

        if (!sapSettings.Value.Enabled)
        {
            // Nothing to check the cards against. The name typed on the page stands for the main card.
            return codes
                .Select(code => new ContactOwner(
                    code,
                    null,
                    string.Equals(code, cardCode, StringComparison.OrdinalIgnoreCase)
                        ? Clean(request.OwnerName) ?? code
                        : code))
                .ToList();
        }

        List<BusinessPartnerDto> partners;
        try
        {
            partners = await sapClient.GetBusinessPartnersByCodesAsync(codes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not confirm {Count} card(s) with SAP before saving a WhatsApp number", codes.Count);
            return Errors.CustomerDocuments.GatewayUnavailable(
                "SAP could not be asked whether these customers exist. Try again in a moment.");
        }

        var owners = new List<ContactOwner>();
        foreach (var code in codes)
        {
            var partner = partners.FirstOrDefault(bp =>
                string.Equals(bp.CardCode?.Trim(), code, StringComparison.OrdinalIgnoreCase));

            if (partner?.CardCode is null)
                return Errors.CustomerDocuments.UnknownCustomer(code);

            owners.Add(new ContactOwner(partner.CardCode.Trim(), null, Clean(partner.CardName) ?? partner.CardCode.Trim()));
        }

        return owners;
    }

    private async Task<ErrorOr<List<ContactOwner>>> ResolveRouteCustomerOwnerAsync(
        int routeCustomerId,
        CancellationToken cancellationToken)
    {
        var routeCustomer = await context.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.Id == routeCustomerId)
            .Select(customer => new { customer.Id, customer.Name, customer.Surname, customer.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (routeCustomer is null)
            return Errors.CustomerDocuments.RouteCustomerNotFound(routeCustomerId);

        if (!routeCustomer.IsActive)
            return Errors.CustomerDocuments.RouteCustomerInactive(routeCustomerId);

        var name = string.Join(" ", new[] { routeCustomer.Name, routeCustomer.Surname }
            .Where(part => !string.IsNullOrWhiteSpace(part))).Trim();

        return new List<ContactOwner> { new(null, routeCustomer.Id, name) };
    }

    private async Task AuditAsync(CustomerWhatsAppContactEntity contact)
    {
        try
        {
            var owner = contact.CardCode ?? $"route customer {contact.RouteCustomerId}";
            await auditService.LogAsync(
                AuditActions.SaveCustomerWhatsAppContact,
                "CustomerWhatsAppContact",
                contact.Id.ToString(),
                $"Saved {WhatsAppRecipients.Mask(contact.PhoneE164)} on {owner} ({contact.OwnerName}); "
                + $"automatic invoices {(contact.AutoSendInvoices ? "on" : "off")}; consent: {contact.ConsentNote ?? "confirmed"}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit WhatsApp contact {ContactId}", contact.Id);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
