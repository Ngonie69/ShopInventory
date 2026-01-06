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
            DocTotal = model.DocTotal,
            VatSum = model.VatSum,
            DocCurrency = model.DocCurrency,
            Lines = model.DocumentLines?.Select(l => l.ToDto()).ToList()
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
            LineTotal = model.LineTotal,
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

    /// <summary>
    /// Maps IncomingPayment model to DTO
    /// </summary>
    public static IncomingPaymentDto ToDto(this IncomingPayment model)
    {
        return new IncomingPaymentDto
        {
            DocEntry = model.DocEntry,
            DocNum = model.DocNum,
            DocDate = model.DocDate,
            DocDueDate = model.DocDueDate,
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
}
