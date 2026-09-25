using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// The price catalogue and item VAT groups are pulled from SAP only from Web → Settings → Data Sync.
/// They used to run every four hours and at 03:45 CAT; a job declared here again would put a sync back
/// behind that page, and QuartzStoredJobReconciler only deletes what the build stops declaring.
/// </summary>
public sealed class MasterDataSyncIsManualTests
{
    private static readonly string[] RetiredJobs = ["price-catalog-sync", "sap-item-tax-group-warm"];

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
    public void The_uom_warm_stays_scheduled()
    {
        // Not a data sync: it pre-resolves UoMs an approval would otherwise resolve on demand.
        Assert.Contains("sap-item-uom-warm", DeclaredJobNames());
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
