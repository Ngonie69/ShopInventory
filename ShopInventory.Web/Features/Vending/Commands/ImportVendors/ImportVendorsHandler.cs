using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Vending.Commands.ImportVendors;

/// <summary>
/// Sends the rows to the API and carries its verdict back.
/// </summary>
/// <remarks>
/// The vendor code convention, the depot each row lands at and every duplicate check live on the API.
/// This does not repeat them: a row-level refusal comes back inside the result for the page to show
/// against its row, and only a request the API would not look at at all becomes an error here.
/// </remarks>
public sealed class ImportVendorsHandler(
    IVendingService vendingService,
    ILogger<ImportVendorsHandler> logger
) : IRequestHandler<ImportVendorsCommand, ErrorOr<ImportVendorsResultModel>>
{
    public async Task<ErrorOr<ImportVendorsResultModel>> Handle(
        ImportVendorsCommand request,
        CancellationToken cancellationToken)
    {
        var (result, error) = await vendingService.ImportVendorsAsync(
            new ImportVendorsRequestModel { ValidateOnly = request.ValidateOnly, Rows = [.. request.Rows] },
            cancellationToken);

        if (result is null)
        {
            return Errors.Vending.ImportFailed(error ?? "The vendors could not be imported.");
        }

        if (result.Imported)
        {
            logger.LogInformation(
                "Imported {CreateCount} new and {RestoreCount} restored vendors from an upload",
                result.CreateCount,
                result.RestoreCount);
        }

        return result;
    }
}
