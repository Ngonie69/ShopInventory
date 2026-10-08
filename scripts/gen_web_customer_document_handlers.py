"""Generates the Web's customer-document MediatR slices: one request record and one handler per
operation, each a single call through ICustomerDocumentService wrapped by CustomerDocumentCall.

Run from the repository root:  python scripts/gen_web_customer_document_handlers.py .
Re-running rewrites the same files byte for byte, so the output can be checked by running it twice.
"""
import os
import sys

ROOT = sys.argv[1]
BASE = os.path.join(ROOT, "ShopInventory.Web", "Features", "CustomerDocuments")

# (folder, name, request parameters, result type, service call, fallback, summary, is_command)
OPERATIONS = [
    ("Queries", "GetCustomerWhatsAppContacts",
     "string? CardCode, int? RouteCustomerId",
     "List<CustomerWhatsAppContactModel>",
     "service.GetContactsAsync(request.CardCode, request.RouteCustomerId, cancellationToken)",
     "The customer's WhatsApp numbers could not be loaded.",
     "The WhatsApp numbers saved on one customer: a SAP card or a route customer."),
    ("Queries", "CheckWhatsAppNumber",
     "string Phone, string? CardCode, int? RouteCustomerId",
     "WhatsAppNumberCheckResultModel",
     "service.CheckPhoneAsync(request.Phone, request.CardCode, request.RouteCustomerId, cancellationToken)",
     "The number could not be checked.",
     "Whether a typed number is one, whether its owner opted out, and whether it is already saved."),
    ("Queries", "GetInvoiceWhatsAppDeliveries",
     "int DocEntry",
     "List<CustomerDocumentDeliveryModel>",
     "service.GetInvoiceDeliveriesAsync(request.DocEntry, cancellationToken)",
     "The invoice's WhatsApp history could not be loaded.",
     "Every WhatsApp send of one invoice, newest first."),
    ("Queries", "PreviewInvoiceWhatsApp",
     "int DocEntry",
     "InvoiceWhatsAppPreviewModel",
     "service.PreviewInvoiceAsync(request.DocEntry, cancellationToken)",
     "The document could not be prepared for a preview.",
     "The PDF and caption sending the invoice would send now, or why it would wait."),
    ("Queries", "GetCustomerDocumentDeliveryLog",
     "string? Status, string? Trigger, string? Search, DateTime? FromDate, DateTime? ToDate, int Page, int PageSize",
     "CustomerDocumentDeliveryPageModel",
     "service.GetLogAsync(request.Status, request.Trigger, request.Search, request.FromDate, request.ToDate, request.Page, request.PageSize, cancellationToken)",
     "The delivery log could not be loaded.",
     "A page of the whole delivery log, for administrators."),
    ("Queries", "GetCustomerDocumentDeliveryStatus",
     "",
     "CustomerDocumentDeliveryStatusModel",
     "service.GetStatusAsync(cancellationToken)",
     "The WhatsApp delivery status could not be loaded.",
     "The switches, today's sends against the caps, and what is waiting."),
    ("Commands", "SaveCustomerWhatsAppContact",
     "SaveCustomerWhatsAppContactModel Contact",
     "List<CustomerWhatsAppContactModel>",
     "service.SaveContactAsync(request.Contact, cancellationToken)",
     "The WhatsApp number could not be saved.",
     "Save a number a customer gave for their documents, with their consent."),
    ("Commands", "UpdateCustomerWhatsAppContact",
     "int ContactId, UpdateCustomerWhatsAppContactModel Changes",
     "CustomerWhatsAppContactModel",
     "service.UpdateContactAsync(request.ContactId, request.Changes, cancellationToken)",
     "The WhatsApp number could not be updated.",
     "Change a saved number's contact name, or whether invoices go to it on their own."),
    ("Commands", "OptOutCustomerWhatsAppContact",
     "int ContactId",
     "List<CustomerWhatsAppContactModel>",
     "service.OptOutContactAsync(request.ContactId, cancellationToken)",
     "The number could not be opted out.",
     "Stop documents to a number on every customer it is saved on."),
    ("Commands", "RecheckCustomerWhatsAppContact",
     "int ContactId",
     "CustomerWhatsAppContactModel",
     "service.RecheckContactAsync(request.ContactId, cancellationToken)",
     "WhatsApp could not be asked about the number.",
     "Ask WhatsApp now whether a saved number has an account."),
    ("Commands", "RemoveCustomerWhatsAppContact",
     "int ContactId",
     "Success",
     None,
     "The number could not be removed.",
     "Take a number off one customer. The record stays for what was sent to it."),
    ("Commands", "RequestInvoiceWhatsApp",
     "int DocEntry, RequestInvoiceWhatsAppModel Recipients",
     "List<CustomerDocumentDeliveryModel>",
     "service.RequestInvoiceAsync(request.DocEntry, request.Recipients, cancellationToken)",
     "The invoice could not be queued for WhatsApp.",
     "Queue an invoice for the customer's WhatsApp. Nothing is sent here; the delivery job sends it."),
    ("Commands", "RetryCustomerDocumentDelivery",
     "long DeliveryId, bool ConfirmNotReceived",
     "CustomerDocumentDeliveryModel",
     "service.RetryAsync(request.DeliveryId, request.ConfirmNotReceived, cancellationToken)",
     "The document could not be sent again.",
     "Send a document again, as a new delivery that records the one it replaces."),
    ("Commands", "CancelCustomerDocumentDelivery",
     "long DeliveryId",
     "CustomerDocumentDeliveryModel",
     "service.CancelAsync(request.DeliveryId, cancellationToken)",
     "The send could not be withdrawn.",
     "Withdraw a document that has not been handed to WhatsApp yet."),
    ("Commands", "UpdateCustomerDocumentDeliverySettings",
     "UpdateCustomerDocumentDeliverySettingsModel Settings",
     "CustomerDocumentDeliveryStatusModel",
     "service.UpdateSettingsAsync(request.Settings, cancellationToken)",
     "The settings could not be saved.",
     "Choose the sending session, switch automatic sending, and set its daily cap."),
]

HEADER = "// Generated by scripts/gen_web_customer_document_handlers.py; edit the generator, not this file.\n"


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def request_file(folder, name, params, result, summary):
    suffix = "Query" if folder == "Queries" else "Command"
    namespace = f"ShopInventory.Web.Features.CustomerDocuments.{folder}.{name}"
    record = f"public sealed record {name}{suffix}({params}) : IRequest<ErrorOr<{result}>>;" if params \
        else f"public sealed record {name}{suffix} : IRequest<ErrorOr<{result}>>;"
    return (
        HEADER
        + "using ErrorOr;\nusing MediatR;\nusing ShopInventory.Web.Models;\n\n"
        + f"namespace {namespace};\n\n"
        + f"/// <summary>{summary}</summary>\n"
        + record + "\n"
    )


def handler_file(folder, name, result, call, fallback, summary):
    suffix = "Query" if folder == "Queries" else "Command"
    namespace = f"ShopInventory.Web.Features.CustomerDocuments.{folder}.{name}"
    if call is None:
        body = (
            "        CustomerDocumentCall.RunAsync<Success>(\n"
            "            async () =>\n"
            "            {\n"
            "                await service.RemoveContactAsync(request.ContactId, cancellationToken);\n"
            "                return Result.Success;\n"
            "            },\n"
        )
    else:
        body = (
            f"        CustomerDocumentCall.RunAsync(\n"
            f"            () => {call},\n"
        )
    return (
        HEADER
        + "using ErrorOr;\nusing MediatR;\nusing ShopInventory.Web.Models;\nusing ShopInventory.Web.Services;\n\n"
        + f"namespace {namespace};\n\n"
        + f"/// <summary>{summary}</summary>\n"
        + f"public sealed class {name}Handler(\n"
        + "    ICustomerDocumentService service,\n"
        + f"    ILogger<{name}Handler> logger\n"
        + f") : IRequestHandler<{name}{suffix}, ErrorOr<{result}>>\n"
        + "{\n"
        + f"    public Task<ErrorOr<{result}>> Handle({name}{suffix} request, CancellationToken cancellationToken) =>\n"
        + body
        + f"            \"{fallback}\",\n"
        + "            logger,\n"
        + f"            \"{name}\");\n"
        + "}\n"
    )


for folder, name, params, result, call, fallback, summary in OPERATIONS:
    suffix = "Query" if folder == "Queries" else "Command"
    directory = os.path.join(BASE, folder, name)
    write(os.path.join(directory, f"{name}{suffix}.cs"), request_file(folder, name, params, result, summary))
    write(os.path.join(directory, f"{name}Handler.cs"), handler_file(folder, name, result, call, fallback, summary))
    print(f"{folder}/{name}")
