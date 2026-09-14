using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Vending.Queries.GetVendorImportTemplate;

/// <summary>
/// Builds the template from a fresh read of the depots, so its next free codes are the ones the API
/// would issue at the moment it was downloaded rather than when the page was opened.
/// </summary>
public sealed class GetVendorImportTemplateHandler(
    IVendingService vendingService,
    IMasterDataCacheService masterDataCache,
    ILogger<GetVendorImportTemplateHandler> logger
) : IRequestHandler<GetVendorImportTemplateQuery, ErrorOr<GetVendorImportTemplateResult>>
{
    public async Task<ErrorOr<GetVendorImportTemplateResult>> Handle(
        GetVendorImportTemplateQuery request,
        CancellationToken cancellationToken)
    {
        Models.VendingOverviewModel overview;
        try
        {
            overview = await vendingService.GetOverviewAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Vending.TemplateFailed(ex.Message);
        }

        var codes = overview.Depots.Select(depot => depot.BusinessPartnerCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Names only. A failed lookup leaves the codes in the Name column, which is still usable.
            foreach (var partner in await masterDataCache.GetBusinessPartnersAsync(cancellationToken: cancellationToken))
            {
                if (partner.CardCode is not null && codes.Contains(partner.CardCode) && !string.IsNullOrWhiteSpace(partner.CardName))
                {
                    names.TryAdd(partner.CardCode, partner.CardName);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Depot names could not be read for the vendor upload template");
        }

        var content = VendorImportWorkbook.BuildTemplate(overview.Depots, names);
        return new GetVendorImportTemplateResult("Vendor_Upload_Template.xlsx", content);
    }
}
