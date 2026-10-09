using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the parts of the API's Quartz registration that decide how much runs at once.
/// </summary>
public class QuartzScheduleShapeTests
{
    [Fact]
    public void The_van_mop_up_is_a_trigger_on_the_posting_job_not_a_job_of_its_own()
    {
        // DisallowConcurrentExecution is enforced per job key. As a job of its own the 19:30 mop-up could
        // run alongside the half-hourly pass, each loading every pending van sale and asking SAP for it.
        var options = Build(new Dictionary<string, string?> { ["VanSalesPosting:Enabled"] = "true" });

        Assert.DoesNotContain(options.JobDetails, job => job.Key.Name == "van-sales-eod-mopup");
        var mopUp = Assert.Single(options.Triggers, trigger => trigger.Key.Name == "van-sales-mopup-trigger");
        Assert.Equal("van-sales-eod-posting", mopUp.JobKey.Name);
    }

    [Fact]
    public void The_scheduler_runs_more_jobs_at_once_than_quartzs_default_ten()
    {
        // About 25 job keys share the pool, several of which can run for minutes when SAP or the fiscal
        // device is slow; with ten threads those starved the 5 and 10 second queue pollers.
        var options = Build([]);

        Assert.Equal("20", options["quartz.threadPool.maxConcurrency"]);
    }

    [Fact]
    public void The_customer_document_job_is_declared_from_appsettings_whatever_OpenWA_says()
    {
        // OpenWA's settings live in each node's web.config. Were the declaration to follow them, a node
        // without the gateway would have QuartzStoredJobReconciler delete the job for the whole cluster.
        var withoutGateway = Build(new Dictionary<string, string?>
        {
            ["CustomerDocuments:Enabled"] = "true",
            ["OpenWA:Enabled"] = "false"
        });
        var switchedOff = Build(new Dictionary<string, string?>
        {
            ["CustomerDocuments:Enabled"] = "false",
            ["OpenWA:Enabled"] = "true"
        });

        Assert.Contains(withoutGateway.JobDetails, job => job.Key.Name == ShopInventory.Services.CustomerDocumentDeliveryJob.JobName);
        Assert.DoesNotContain(switchedOff.JobDetails, job => job.Key.Name == ShopInventory.Services.CustomerDocumentDeliveryJob.JobName);
    }

    [Fact]
    public void The_customer_document_job_never_runs_twice_at_once()
    {
        // Half of the guard against sending a document twice; the conditional claim is the other half.
        Assert.True(Attribute.IsDefined(typeof(ShopInventory.Services.CustomerDocumentDeliveryJob), typeof(DisallowConcurrentExecutionAttribute)));
    }

    [Fact]
    public void The_invoice_scan_is_declared_only_with_SAP_and_customer_documents_both_on()
    {
        // It reads SAP and needs no gateway, so OpenWA does not come into it either way.
        var both = Build(new Dictionary<string, string?>
        {
            ["CustomerDocuments:Enabled"] = "true",
            ["SAP:Enabled"] = "true",
            ["OpenWA:Enabled"] = "false"
        });
        var noSap = Build(new Dictionary<string, string?>
        {
            ["CustomerDocuments:Enabled"] = "true",
            ["SAP:Enabled"] = "false"
        });
        var noDocuments = Build(new Dictionary<string, string?>
        {
            ["CustomerDocuments:Enabled"] = "false",
            ["SAP:Enabled"] = "true"
        });

        Assert.Contains(both.JobDetails, job => job.Key.Name == ShopInventory.Services.CustomerInvoiceScanJob.JobName);
        Assert.DoesNotContain(noSap.JobDetails, job => job.Key.Name == ShopInventory.Services.CustomerInvoiceScanJob.JobName);
        Assert.DoesNotContain(noDocuments.JobDetails, job => job.Key.Name == ShopInventory.Services.CustomerInvoiceScanJob.JobName);
        Assert.True(Attribute.IsDefined(typeof(ShopInventory.Services.CustomerInvoiceScanJob), typeof(DisallowConcurrentExecutionAttribute)));
    }

    private static QuartzOptions Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddShopInventoryQuartz(configuration, "Host=unused");

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<QuartzOptions>>().Value;
    }
}
