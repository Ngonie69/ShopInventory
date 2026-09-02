using ErrorOr;
using MediatR;
using ShopInventory.Common.Validation;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.ValidateTransfer;

public sealed class ValidateTransferHandler(
    ApplicationDbContext context,
    IStockValidationService stockValidation,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<ValidateTransferCommand, ErrorOr<ValidateTransferResult>>
{
    public async Task<ErrorOr<ValidateTransferResult>> Handle(
        ValidateTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var request = command.Request;

        var sapRequest = new CreateInventoryTransferRequest
        {
            FromWarehouse = request.FromWarehouse,
            ToWarehouse = request.ToWarehouse,
            Lines = request.Lines.Select(l => new CreateInventoryTransferLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                UoMCode = l.UoMCode,
                FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                ToWarehouseCode = l.WarehouseCode ?? request.ToWarehouse,
            }).ToList()
        };

        var quantityErrors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            sapRequest.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken);

        if (quantityErrors.Count > 0)
            return Errors.DesktopIntegration.ValidationFailed(string.Join("; ", quantityErrors));

        var result = await stockValidation.ValidateInventoryTransferStockAsync(sapRequest, cancellationToken);

        // The caller is asking whether this transfer would post. A warehouse SAP never answered
        // for was not measured, and reporting that as a pass is the answer they cannot act on.
        if (result.IsValid && !result.StockWasFullyRead)
        {
            return new ValidateTransferResult(
                IsValid: false,
                Message: "Could not read stock from SAP for warehouse(s) " +
                         string.Join(", ", result.UnreadableWarehouses),
                Errors: [],
                LinesValidated: request.Lines.Count);
        }

        return new ValidateTransferResult(
            IsValid: result.IsValid,
            Message: result.IsValid ? "Transfer validation successful" : "Transfer validation failed",
            Errors: result.Errors.Select(e => new ValidateTransferErrorItem(e.ItemCode, e.WarehouseCode, e.Message)).ToList(),
            LinesValidated: request.Lines.Count
        );
    }
}
