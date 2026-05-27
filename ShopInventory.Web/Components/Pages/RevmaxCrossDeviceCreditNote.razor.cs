using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;
using ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteDraft;

namespace ShopInventory.Web.Components.Pages;

public partial class RevmaxCrossDeviceCreditNote : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private ILogger<RevmaxCrossDeviceCreditNote> Logger { get; set; } = default!;

    private GetCrossDeviceCreditNoteDraftResult? draft;
    private FiscalizeCrossDeviceCreditNoteResult? submissionResult;

    private string creditNoteNumber = string.Empty;
    private string originalInvoiceNumber = string.Empty;
    private string? currency;
    private string? branchName;
    private string? customerName;
    private string? customerVatNumber;
    private string? customerAddress;
    private string? customerTelephone;
    private string? customerEmail;
    private string? customerBpn;
    private string invoiceComment = string.Empty;
    private int? refDeviceId;
    private long? refReceiptGlobalNo;
    private int? refFiscalDayNo;
    private bool isLoadingDraft;
    private bool isSubmitting;
    private string? errorMessage;
    private string? warningMessage;
    private string? LookupGuidanceMessage => BuildLookupGuidanceMessage();

    private bool HasReferenceOverrides => refDeviceId.HasValue && refReceiptGlobalNo.HasValue && refFiscalDayNo.HasValue;

    private bool CanSubmit => draft is not null
        && !isLoadingDraft
        && !isSubmitting
        && !string.IsNullOrWhiteSpace(creditNoteNumber)
        && !string.IsNullOrWhiteSpace(originalInvoiceNumber)
        && !string.IsNullOrWhiteSpace(invoiceComment)
        && (draft.SourceFiscalInvoice?.Success == true || HasReferenceOverrides);

    private async Task LoadDraftAsync()
    {
        errorMessage = null;
        warningMessage = null;
        submissionResult = null;
        creditNoteNumber = creditNoteNumber.Trim();

        if (string.IsNullOrWhiteSpace(creditNoteNumber))
        {
            Snackbar.Add("Enter a credit note number first.", MudBlazor.Severity.Warning);
            return;
        }

        isLoadingDraft = true;

        try
        {
            var result = await Mediator.Send(new GetCrossDeviceCreditNoteDraftQuery(creditNoteNumber));

            result.SwitchFirst(
                value => ApplyDraft(value),
                error =>
                {
                    draft = null;
                    errorMessage = error.Description;
                });

            if (!result.IsError && !string.IsNullOrWhiteSpace(warningMessage))
            {
                Snackbar.Add(warningMessage, MudBlazor.Severity.Warning);
            }
        }
        catch (ValidationException ex)
        {
            draft = null;
            errorMessage = ex.Errors.FirstOrDefault()?.ErrorMessage ?? "Enter a valid credit note number.";
            Snackbar.Add(errorMessage, MudBlazor.Severity.Warning);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load REVMax cross-device credit note draft");
            draft = null;
            errorMessage = "Failed to load the cross-device credit note draft.";
            Snackbar.Add(errorMessage, MudBlazor.Severity.Error);
        }
        finally
        {
            isLoadingDraft = false;
        }
    }

    private async Task FiscalizeAsync()
    {
        creditNoteNumber = creditNoteNumber.Trim();
        originalInvoiceNumber = originalInvoiceNumber.Trim();

        if (!CanSubmit)
        {
            Snackbar.Add("Load the credit note and complete the required fiscalization fields first.", MudBlazor.Severity.Warning);
            return;
        }

        isSubmitting = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(new FiscalizeCrossDeviceCreditNoteCommand(
                creditNoteNumber,
                originalInvoiceNumber,
                refDeviceId,
                refReceiptGlobalNo,
                refFiscalDayNo,
                currency,
                branchName,
                customerName,
                customerVatNumber,
                customerAddress,
                customerTelephone,
                customerEmail,
                customerBpn,
                invoiceComment));

            result.SwitchFirst(
                value =>
                {
                    submissionResult = value;
                    warningMessage = null;
                    Snackbar.Add($"Credit note {value.FiscalInvoiceNumber} fiscalized successfully.", MudBlazor.Severity.Success);
                },
                error =>
                {
                    submissionResult = null;
                    errorMessage = error.Description;
                    Snackbar.Add(error.Description, MudBlazor.Severity.Error);
                });
        }
        catch (ValidationException ex)
        {
            var message = ex.Errors.FirstOrDefault()?.ErrorMessage ?? "The fiscalization request is invalid.";
            errorMessage = message;
            Snackbar.Add(message, MudBlazor.Severity.Warning);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to fiscalize cross-device credit note {CreditNoteNumber}", creditNoteNumber);
            errorMessage = "Failed to fiscalize the credit note.";
            Snackbar.Add(errorMessage, MudBlazor.Severity.Error);
        }
        finally
        {
            isSubmitting = false;
        }
    }

    private void ClearForm()
    {
        draft = null;
        submissionResult = null;
        errorMessage = null;
        warningMessage = null;
        creditNoteNumber = string.Empty;
        originalInvoiceNumber = string.Empty;
        currency = null;
        branchName = null;
        customerName = null;
        customerVatNumber = null;
        customerAddress = null;
        customerTelephone = null;
        customerEmail = null;
        customerBpn = null;
        invoiceComment = string.Empty;
        refDeviceId = null;
        refReceiptGlobalNo = null;
        refFiscalDayNo = null;
    }

    private void ApplyDraft(GetCrossDeviceCreditNoteDraftResult value)
    {
        draft = value;
        errorMessage = null;
        warningMessage = BuildWarningMessage(value);
        originalInvoiceNumber = value.SourceInvoiceNumber ?? string.Empty;
        currency = ChooseFormValue(currency, value.SuggestedCurrency ?? value.CreditNote.Currency);
        branchName = ChooseFormValue(branchName, value.SuggestedBranchName);
        customerName = ChooseFormValue(customerName, value.SuggestedCustomerName);
        customerVatNumber = ChooseFormValue(customerVatNumber, value.SuggestedCustomerVatNumber);
        customerAddress = ChooseFormValue(customerAddress, value.SuggestedCustomerAddress);
        customerTelephone = ChooseFormValue(customerTelephone, value.SuggestedCustomerTelephone);
        customerEmail = ChooseFormValue(customerEmail, value.SuggestedCustomerEmail);
        customerBpn = ChooseFormValue(customerBpn, value.SuggestedCustomerBpn);
        invoiceComment = string.IsNullOrWhiteSpace(invoiceComment)
            ? value.SuggestedInvoiceComment ?? string.Empty
            : invoiceComment;
        refDeviceId = value.SuggestedRefDeviceId;
        refReceiptGlobalNo = value.SuggestedRefReceiptGlobalNo;
        refFiscalDayNo = value.SuggestedRefFiscalDayNo;
    }

    private string FormatSourceInvoiceTotal()
    {
        if (draft?.SourceFiscalInvoice?.Data is null)
        {
            return "Not available";
        }

        return $"{DisplayValue(draft.SourceFiscalInvoice.Data.ReceiptCurrency)} {Math.Abs(draft.SourceFiscalInvoice.Data.ReceiptTotal):N2}";
    }

    private static string FormatMoney(decimal amount, string? currencyCode)
        => $"{DisplayValue(currencyCode)} {Math.Abs(amount):N2}";

    private static string FormatQuantity(decimal quantity)
        => Math.Abs(quantity).ToString("N2");

    private static string DisplayValue(object? value)
    {
        if (value is null)
        {
            return "Not available";
        }

        return value switch
        {
            string text when string.IsNullOrWhiteSpace(text) => "Not available",
            string text => text,
            _ => value.ToString() ?? "Not available"
        };
    }

    private static string? ChooseFormValue(string? current, string? suggested)
        => string.IsNullOrWhiteSpace(current) ? suggested : current;

    private static string? BuildWarningMessage(GetCrossDeviceCreditNoteDraftResult value)
    {
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(value.SourceFiscalInvoiceError))
        {
            warnings.Add(value.SourceFiscalInvoiceError);
        }

        if (!string.IsNullOrWhiteSpace(value.CurrentDeviceError))
        {
            warnings.Add(value.CurrentDeviceError);
        }

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    private string GetCreditNoteLookupStatusText()
    {
        if (draft is not null)
        {
            return "Loaded";
        }

        if (isLoadingDraft)
        {
            return "Searching";
        }

        return string.IsNullOrWhiteSpace(creditNoteNumber) ? "Required" : "Ready";
    }

    private string GetCreditNoteLookupStatusClass()
    {
        if (draft is not null)
        {
            return "rxcn-status-ok";
        }

        return string.IsNullOrWhiteSpace(creditNoteNumber)
            ? "rxcn-status-muted"
            : "rxcn-status-warn";
    }

    private string GetOriginalInvoiceStatusText()
    {
        if (draft?.SourceFiscalInvoice?.Success == true)
        {
            return "Resolved";
        }

        if (!string.IsNullOrWhiteSpace(draft?.SourceInvoiceNumber) || draft?.OriginalInvoice is not null)
        {
            return "Linked";
        }

        return draft is null ? "Waiting" : "Needed";
    }

    private string GetOriginalInvoiceStatusClass()
    {
        if (draft?.SourceFiscalInvoice?.Success == true
            || !string.IsNullOrWhiteSpace(draft?.SourceInvoiceNumber)
            || draft?.OriginalInvoice is not null)
        {
            return "rxcn-status-ok";
        }

        return draft is not null
            ? "rxcn-status-warn"
            : "rxcn-status-muted";
    }

    private string GetOriginalInvoiceStepCopy()
    {
        if (draft?.SourceFiscalInvoice?.Success == true)
        {
            return $"Source invoice {draft.SourceInvoiceNumber} is loaded from REVMax and ready for review.";
        }

        if (!string.IsNullOrWhiteSpace(draft?.SourceInvoiceNumber))
        {
            return $"Original invoice {draft.SourceInvoiceNumber} was resolved automatically from the linked credit note.";
        }

        return "Resolved automatically from the linked invoice after loading the credit note.";
    }

    private string GetReferenceStatusText()
    {
        if (draft?.SourceFiscalInvoice?.Success == true && HasReferenceOverrides)
        {
            return "Auto-filled";
        }

        if (HasReferenceOverrides)
        {
            return "Manual";
        }

        return draft is null ? "Waiting" : "Needed";
    }

    private string GetReferenceStatusClass()
    {
        if (HasReferenceOverrides)
        {
            return "rxcn-status-ok";
        }

        return draft is null ? "rxcn-status-muted" : "rxcn-status-warn";
    }

    private string GetReferenceStepCopy()
    {
        if (draft?.SourceFiscalInvoice?.Success == true && HasReferenceOverrides)
        {
            return "Device ID, receipt global number, and fiscal day were copied from the source REVMax invoice.";
        }

        if (HasReferenceOverrides)
        {
            return "Manual cross-device reference values are present and ready for submission.";
        }

        return "Needs the source REVMax invoice or manual reference values from the original device.";
    }

    private string GetResolvedCreditNoteCopy()
    {
        if (draft is null)
        {
            return string.Empty;
        }

        return $"Lookup used: {DisplayValue(creditNoteNumber)}. SAP DocNum: {DisplayValue(draft.CreditNote.SAPDocNum)}.";
    }

    private string? BuildLookupGuidanceMessage()
    {
        if (string.IsNullOrWhiteSpace(errorMessage)
            || !errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TryParseSapDocNum(creditNoteNumber) is int sapDocNum)
        {
            return $"Accepted lookups: the local credit note number, SAP DocNum {sapDocNum}, or SAP-CN-{sapDocNum}.";
        }

        return "Accepted lookups: the local credit note number, the raw SAP DocNum, or SAP-CN-{DocNum}.";
    }

    private static int? TryParseSapDocNum(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, out var rawDocNum))
        {
            return rawDocNum;
        }

        const string sapPrefix = "SAP-CN-";
        if (trimmed.StartsWith(sapPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[sapPrefix.Length..], out var prefixedDocNum))
        {
            return prefixedDocNum;
        }

        return null;
    }
}