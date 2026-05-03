using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.UserManagement.Queries.GetDriverBusinessPartnerAccess;

public sealed class GetDriverBusinessPartnerAccessHandler(
    IAppSettingsService appSettingsService,
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    ILogger<GetDriverBusinessPartnerAccessHandler> logger
) : IRequestHandler<GetDriverBusinessPartnerAccessQuery, ErrorOr<GetDriverBusinessPartnerAccessResult>>
{
    public async Task<ErrorOr<GetDriverBusinessPartnerAccessResult>> Handle(
        GetDriverBusinessPartnerAccessQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var customersTask = dbContext.CachedBusinessPartners
                .AsNoTracking()
                .Where(businessPartner => businessPartner.CardType == "cCustomer" || businessPartner.CardType == "C")
                .OrderBy(businessPartner => businessPartner.CardCode)
                .Select(businessPartner => new BusinessPartnerDto
                {
                    CardCode = businessPartner.CardCode,
                    CardName = businessPartner.CardName,
                    CardType = businessPartner.CardType,
                    GroupCode = businessPartner.GroupCode,
                    Phone1 = businessPartner.Phone1,
                    Phone2 = businessPartner.Phone2,
                    Email = businessPartner.Email,
                    Address = businessPartner.Address,
                    City = businessPartner.City,
                    Country = businessPartner.Country,
                    Currency = businessPartner.Currency,
                    Balance = businessPartner.Balance,
                    IsActive = businessPartner.IsActive,
                    PriceListNum = businessPartner.PriceListNum
                })
                .ToListAsync(cancellationToken);
            var settingTask = appSettingsService.GetValueAsync(SettingKeys.DriverVisibleBusinessPartners);

            await Task.WhenAll(customersTask, settingTask);

            return new GetDriverBusinessPartnerAccessResult
            {
                Customers = await customersTask,
                AssignedCustomerCodes = ParseAssignedCustomerCodes(await settingTask)
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to load global driver business partner access");
            return Errors.UserManagement.GetDriverBusinessPartnerAccessFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error loading global driver business partner access");
            return Errors.UserManagement.GetDriverBusinessPartnerAccessFailed("Failed to load driver business partner access.");
        }
    }

    private static List<string> ParseAssignedCustomerCodes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(value) ?? new List<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}