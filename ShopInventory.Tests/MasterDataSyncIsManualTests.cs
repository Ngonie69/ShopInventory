using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Controllers;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// The price catalogue, item VAT groups and item UoM resolutions are pulled from SAP only from Web →
/// Settings → Data Sync. They used to run every four hours and at 03:45 and 03:30 CAT; a job declared here again would put a sync back
/// behind that page, and QuartzStoredJobReconciler only deletes what the build stops declaring.
/// </summary>
public sealed class MasterDataSyncIsManualTests
{
    private static readonly string[] RetiredJobs = ["price-catalog-sync", "sap-item-tax-group-warm", "sap-item-uom-warm"];

    [Fact]
    public void Neither_master_data_sync_is_scheduled()
    {
        var declared = DeclaredJobNames(new() { ["SAP:Enabled"] = "true" });

        Assert.NotEmpty(declared);
        Assert.All(RetiredJobs, job => Assert.DoesNotContain(job, declared));
    }

    [Fact]
    public void The_old_price_schedule_settings_bring_nothing_back()
    {
        // What a production config written before the change still carries.
        var declared = DeclaredJobNames(new()
        {
            ["SAP:Enabled"] = "true",
            ["SAP:AutoSyncEnabled"] = "true",
            ["SAP:SyncIntervalHours"] = "4",
            ["SAP:InitialDelayMinutes"] = "0"
        });

        Assert.All(RetiredJobs, job => Assert.DoesNotContain(job, declared));
    }

    [Fact]
    public void The_uom_sync_Data_Sync_calls_is_an_admin_background_endpoint()
    {
        var action = typeof(SyncController).GetMethod(nameof(SyncController.WarmItemUoms))!;

        Assert.Equal("item-uoms", Assert.Single(action.GetCustomAttributes<HttpPostAttribute>()).Template);
        Assert.Equal("Admin", Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>()).Roles);

        // Its purpose is to keep approvals fast, so it must not hold their reserved SAP slots.
        Assert.NotNull(action.GetCustomAttribute<SapBackgroundWorkAttribute>());
    }

    private static HashSet<string> DeclaredJobNames(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddShopInventoryQuartz(configuration, "Host=unused");

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<QuartzOptions>>().Value.JobDetails
            .Select(job => job.Key.Name)
            .ToHashSet();
    }
}
