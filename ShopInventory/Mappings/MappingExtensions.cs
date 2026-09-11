using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Mappings;

public static class MappingExtensions
{
    /// <summary>
    /// Maps InventoryTransfer model to DTO
    /// </summary>
    public static InventoryTransferDto ToDto(this InventoryTransfer model)
    {
        return new InventoryTransferDto
        {
            DocEntry = model.DocEntry,
            DocNum = model.DocNum,
            DocDate = model.DocDate,
            DueDate = model.DueDate,
            FromWarehouse = model.FromWarehouse,
            ToWarehouse = model.ToWarehouse,
            Comments = model.Comments,
            Lines = model.StockTransferLines?.Select(l => l.ToDto()).ToList()
        };
    }

    /// <summary>
    /// Maps InventoryTransferLine model to DTO
    /// </summary>
    public static InventoryTransferLineDto ToDto(this InventoryTransferLine model)
    {
        return new InventoryTransferLineDto
        {
            LineNum = model.LineNum,
            ItemCode = model.ItemCode,
            ItemDescription = model.ItemDescription,
            Quantity = model.Quantity,
            FromWarehouseCode = model.FromWarehouseCode,
            ToWarehouseCode = model.WarehouseCode,
            UoMCode = model.UoMCode
        };
    }

    /// <summary>
    /// Maps a list of InventoryTransfer models to DTOs
    /// </summary>
    public static List<InventoryTransferDto> ToDto(this List<InventoryTransfer> models)
    {
        return models.Select(m => m.ToDto()).ToList();
    }

    /// <summary>
    /// Maps Invoice model to DTO
    /// </summary>
    public static InvoiceDto ToDto(this Invoice model)
    {
        return new InvoiceDto
        {
            DocEntry = model.DocEntry,
            DocNum = model.DocNum,
            DocDate = model.DocDate,
            DocDueDate = model.DocDueDate,
            CardCode = model.CardCode,
            CardName = model.CardName,
            NumAtCard = model.NumAtCard,
            Comments = model.Comments,
            DocStatus = NormalizeInvoiceStatus(model.DocStatus, model.DocumentStatus, model.Cancelled),
            VanSaleOrderNumber = model.U_Van_saleorder,
            IsVanSalesInvoice = !string.IsNullOrWhiteSpace(model.U_Van_saleorder),
            DocTotal = model.DocTotal,
            PaidToDate = model.PaidToDate,
            VatSum = model.VatSum,
            DocCurrency = model.DocCurrency,
            BillToAddress = model.Address,
            ShipToAddress = model.Address2,
            Lines = model.DocumentLines?.Select(l => l.ToDto()).ToList()
        };
    }

    /// <summary>
    /// The document a credit note is fiscalised from, as SAP holds it.
    /// </summary>
    /// <remarks>
    /// One mapping for every route that fiscalises a credit note it has just raised or added. SAP
    /// returns a credit note's amounts as magnitudes on some reads and negative on others, so they are
    /// made positive here and the receipt type carries the sign.
    ///
    /// <para>
    /// The gross price and the VAT group are the point of it. A receipt declares what the customer
    /// paid, and REVMax prices every line from it; without them each line fell back to SAP's NET unit
    /// price, so the lines summed short of the credited total by the VAT and the credit note was refused
    /// as unreconciled — 356.25 of lines against 411.47 on till invoice 772109. The tax code likewise
    /// lives in <c>VatGroup</c>; <c>TaxCode</c> comes back null.
    /// </para>
    /// </remarks>
    public static InvoiceDto ToFiscalDocument(this SAPCreditNote creditNote, string? comments)
    {
        return new InvoiceDto
        {
            DocEntry = creditNote.DocEntry,
            DocNum = creditNote.DocNum,
            CardCode = creditNote.CardCode,
            CardName = creditNote.CardName,
            DocTotal = Math.Abs(creditNote.DocTotal),
            VatSum = Math.Abs(creditNote.VatSum),
            DocCurrency = creditNote.DocCurrency,
            Comments = comments,
            Lines = creditNote.DocumentLines?.Select(line => new InvoiceLineDto
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = Math.Abs(line.Quantity),
                UnitPrice = line.UnitPrice,
                GrossPrice = Math.Abs(line.GrossPrice),
                PriceAfterVat = Math.Abs(line.PriceAfterVAT),
                GrossTotal = Math.Abs(line.GrossTotal),
                VatGroup = line.VatGroup,
                LineTotal = Math.Abs(line.LineTotal),
                TaxCode = line.TaxCode,
                WarehouseCode = line.WarehouseCode,
                DiscountPercent = line.DiscountPercent ?? 0m,
                UoMCode = line.UoMCode
            }).ToList()
        };
    }

    /// <summary>
    /// Maps InvoiceLine model to DTO
    /// </summary>
    public static InvoiceLineDto ToDto(this InvoiceLine model)
    {
        return new InvoiceLineDto
        {
            LineNum = model.LineNum,
            ItemCode = model.ItemCode,
            ItemDescription = model.ItemDescription,
            Quantity = model.Quantity,
            UnitPrice = model.UnitPrice,
            GrossPrice = model.GrossPrice,
            PriceAfterVat = model.PriceAfterVAT,
            GrossTotal = model.GrossTotal,
            VatGroup = model.VatGroup,
            LineTotal = model.LineTotal,
            TaxCode = model.TaxCode,
            WarehouseCode = model.WarehouseCode,
            DiscountPercent = model.DiscountPercent,
            UoMCode = model.UoMCode
        };
    }

    /// <summary>
    /// Maps a list of Invoice models to DTOs
    /// </summary>
    public static List<InvoiceDto> ToDto(this List<Invoice> models)
    {
        return models.Select(m => m.ToDto()).ToList();
    }

    private static string? NormalizeInvoiceStatus(string? docStatus, string? documentStatus, string? cancelled)
    {
        if (string.Equals(cancelled, "tYES", StringComparison.OrdinalIgnoreCase))
            return "X";

        var status = !string.IsNullOrWhiteSpace(docStatus) ? docStatus : documentStatus;
        return status switch
        {
            "bost_Open" => "O",
            "bost_Close" => "C",
            _ => status
        };
    }

    /// <summary>
    /// Maps IncomingPayment model to DTO
    /// </summary>
    public static IncomingPaymentDto ToDto(this IncomingPayment model)
    {
        // IncomingPayment.DocTotal is composed from the four means of payment; SAP has no header
        // total on a payment. The fallback that used to live here could not work — it added
        // CheckSum and CreditSum, which were bound to fields SAP does not have and were always zero.
        return new IncomingPaymentDto
        {
            DocEntry = model.DocEntry,
            DocNum = model.DocNum,
            DocDate = model.DocDate,
            DocDueDate = model.DueDate,
            CardCode = model.CardCode,
            CardName = model.CardName,
            DocCurrency = model.DocCurrency,
            CashSum = model.CashSum,
            CheckSum = model.CheckSum,
            TransferSum = model.TransferSum,
            CreditSum = model.CreditSum,
            DocTotal = model.DocTotal,
            Remarks = model.Remarks,
            TransferReference = model.TransferReference,
            TransferDate = model.TransferDate,
            TransferAccount = model.TransferAccount,
            PaymentInvoices = model.PaymentInvoices?.Select(l => l.ToDto()).ToList(),
            PaymentChecks = model.PaymentChecks?.Select(l => l.ToDto()).ToList(),
            PaymentCreditCards = model.PaymentCreditCards?.Select(l => l.ToDto()).ToList()
        };
    }

    /// <summary>
    /// Maps PaymentInvoice model to DTO
    /// </summary>
    public static PaymentInvoiceDto ToDto(this PaymentInvoice model)
    {
        return new PaymentInvoiceDto
        {
            LineNum = model.LineNum,
            DocEntry = model.DocEntry,
            SumApplied = model.SumApplied,
            SumAppliedFC = model.SumAppliedFC,
            InvoiceType = model.InvoiceType
        };
    }

    /// <summary>
    /// Maps PaymentCheck model to DTO
    /// </summary>
    public static PaymentCheckDto ToDto(this PaymentCheck model)
    {
        return new PaymentCheckDto
        {
            LineNum = model.LineNum,
            DueDate = model.DueDate,
            CheckNumber = model.CheckNumber,
            BankCode = model.BankCode,
            Branch = model.Branch,
            AccountNum = model.AccountNum,
            CheckSum = model.CheckSum,
            Currency = model.Currency
        };
    }

    /// <summary>
    /// Maps PaymentCreditCard model to DTO
    /// </summary>
    public static PaymentCreditCardDto ToDto(this PaymentCreditCard model)
    {
        return new PaymentCreditCardDto
        {
            LineNum = model.LineNum,
            CreditCard = model.CreditCard,
            CreditCardNumber = model.CreditCardNumber,
            CardValidUntil = model.CardValidUntil,
            VoucherNum = model.VoucherNum,
            CreditSum = model.CreditSum,
            CreditCur = model.CreditCur
        };
    }

    /// <summary>
    /// Maps a list of IncomingPayment models to DTOs
    /// </summary>
    public static List<IncomingPaymentDto> ToDto(this List<IncomingPayment> models)
    {
        return models.Select(m => m.ToDto()).ToList();
    }

    #region Inventory Transfer Request Mappings

    /// <summary>
    /// Maps InventoryTransferRequest model to DTO
    /// </summary>
    public static InventoryTransferRequestDto ToDto(this InventoryTransferRequest model)
    {
        return new InventoryTransferRequestDto
        {
            DocEntry = model.DocEntry,
            DocNum = model.DocNum,
            DocDate = model.DocDate,
            DueDate = model.DueDate,
            FromWarehouse = model.FromWarehouse,
            ToWarehouse = model.ToWarehouse,
            Comments = model.Comments,
            DocumentStatus = model.DocumentStatus,
            RequesterEmail = model.RequesterEmail,
            RequesterName = model.RequesterName,
            RequesterBranch = model.RequesterBranch,
            RequesterDepartment = model.RequesterDepartment,
            Lines = model.StockTransferLines?.Select(l => l.ToDto()).ToList()
        };
    }

    /// <summary>
    /// Maps InventoryTransferRequestLine model to DTO
    /// </summary>
    public static InventoryTransferRequestLineDto ToDto(this InventoryTransferRequestLine model)
    {
        return new InventoryTransferRequestLineDto
        {
            LineNum = model.LineNum,
            ItemCode = model.ItemCode,
            ItemDescription = model.ItemDescription,
            Quantity = model.Quantity,
            FromWarehouseCode = model.FromWarehouseCode,
            ToWarehouseCode = model.WarehouseCode,
            UoMCode = model.UoMCode
        };
    }

    /// <summary>
    /// Maps a list of InventoryTransferRequest models to DTOs
    /// </summary>
    public static List<InventoryTransferRequestDto> ToDto(this List<InventoryTransferRequest> models)
    {
        return models.Select(m => m.ToDto()).ToList();
    }

    #endregion
}
