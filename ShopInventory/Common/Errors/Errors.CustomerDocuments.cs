using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>Sending customers their documents on WhatsApp, and the register of their numbers.</summary>
    public static class CustomerDocuments
    {
        public static readonly Error Disabled =
            Error.Validation("CustomerDocuments.Disabled",
                "Sending documents on WhatsApp is switched off on this server.");

        public static readonly Error SessionNotConfigured =
            Error.Validation("CustomerDocuments.SessionNotConfigured",
                "No WhatsApp number has been chosen to send documents from. An administrator sets it on the WhatsApp Deliveries page.");

        public static readonly Error UserNotFound =
            Error.NotFound("CustomerDocuments.UserNotFound", "Your account could not be found.");

        public static Error InvalidPhone(string phone) =>
            Error.Validation("CustomerDocuments.InvalidPhone",
                $"'{phone}' is not a phone number. Write it as 0771234567 or +263771234567.");

        public static readonly Error OwnerRequired =
            Error.Validation("CustomerDocuments.OwnerRequired",
                "Say which customer the number belongs to: a customer code or a route customer, not both.");

        public static Error SellingAccountNotAllowed(string cardCode) =>
            Error.Validation("CustomerDocuments.SellingAccountNotAllowed",
                $"{cardCode} is the account a till, van or vendor sells as, not a customer. A number saved on it would receive every invoice that account posts.");

        public static Error RouteCustomerNotFound(int routeCustomerId) =>
            Error.NotFound("CustomerDocuments.RouteCustomerNotFound", $"Route customer {routeCustomerId} was not found.");

        public static Error RouteCustomerInactive(int routeCustomerId) =>
            Error.Validation("CustomerDocuments.RouteCustomerInactive",
                $"Route customer {routeCustomerId} is inactive. Reactivate it before saving a number on it.");

        public static Error UnknownCustomer(string cardCode) =>
            Error.Validation("CustomerDocuments.UnknownCustomer", $"SAP has no customer '{cardCode}'.");

        public static readonly Error ConsentRequired =
            Error.Validation("CustomerDocuments.ConsentRequired",
                "Confirm the customer agreed to receive their documents on this WhatsApp number.");

        public static Error ContactLimitReached(int limit) =>
            Error.Validation("CustomerDocuments.ContactLimitReached",
                $"A customer may have at most {limit} WhatsApp numbers. Remove one first.");

        public static Error ContactNotFound(int id) =>
            Error.NotFound("CustomerDocuments.ContactNotFound", $"WhatsApp contact {id} was not found.");

        public static Error NumberOptedOut(string maskedPhone) =>
            Error.Validation("CustomerDocuments.NumberOptedOut",
                $"{maskedPhone} asked not to receive documents. Save it again on the customer with fresh consent before sending to it.");

        public static readonly Error RecipientRequired =
            Error.Validation("CustomerDocuments.RecipientRequired",
                "Choose at least one saved number, or type the number to send to.");

        public static Error OneOffLimitReached(int limit) =>
            Error.Validation("CustomerDocuments.OneOffLimitReached",
                $"You have sent documents to {limit} unsaved numbers today, the most allowed. Save the number on the customer instead.");

        public static Error VanSaleNotFound(string vanOrder) =>
            Error.NotFound("CustomerDocuments.VanSaleNotFound",
                $"No sale under {vanOrder} was found on this van. If it was made offline it has not reached the office yet; try again once it has uploaded.");

        public static Error VanSaleSendLimitReached(int limit) =>
            Error.Validation("CustomerDocuments.VanSaleSendLimitReached",
                $"You have sent {limit} invoices on WhatsApp today, the most allowed. The office can send this one from the invoice.");

        public static Error InvoiceNotFound(int docEntry) =>
            Error.NotFound("CustomerDocuments.InvoiceNotFound", $"Invoice {docEntry} was not found in SAP.");

        public static Error InvoiceCancelled(int docNum) =>
            Error.Validation("CustomerDocuments.InvoiceCancelled",
                $"Invoice {docNum} is cancelled, or is the document that cancels another; it is not sent to customers.");

        public static Error ConsolidatedNotSendable(int docNum) =>
            Error.Validation("CustomerDocuments.ConsolidatedNotSendable",
                $"Invoice {docNum} is an end-of-day consolidation of many sales; each customer holds their own receipt for their sale, so it is not sent.");

        public static Error RepostedNotSendable(int docNum, string? oldInvoiceNumber) =>
            Error.Validation("CustomerDocuments.RepostedNotSendable",
                oldInvoiceNumber is null
                    ? $"Invoice {docNum} was reposted after the SAP update; its fiscal receipt is under its old number, so it is not sent."
                    : $"Invoice {docNum} was reposted after the SAP update from invoice {oldInvoiceNumber}; its fiscal receipt is under the old number, so it is not sent.");

        public static Error DeliveryNotFound(long id) =>
            Error.NotFound("CustomerDocuments.DeliveryNotFound", $"Delivery {id} was not found.");

        public static Error DeliveryNotRetryable(string status) =>
            Error.Conflict("CustomerDocuments.DeliveryNotRetryable",
                $"A delivery that is {status} cannot be sent again from here.");

        public static readonly Error ReceiptNotConfirmed =
            Error.Validation("CustomerDocuments.ReceiptNotConfirmed",
                "This document may already have reached the customer. Confirm they did not receive it before sending it again.");

        public static Error DeliveryNotCancellable(string status) =>
            Error.Conflict("CustomerDocuments.DeliveryNotCancellable",
                $"A delivery that is {status} can no longer be withdrawn.");

        public static Error SettingsInvalid(string message) =>
            Error.Validation("CustomerDocuments.SettingsInvalid", message);

        public static Error GatewayUnavailable(string message) =>
            Error.Failure("CustomerDocuments.GatewayUnavailable", message);

        public static Error DocumentUnavailable(string message) =>
            Error.Failure("CustomerDocuments.DocumentUnavailable", message);
    }
}
