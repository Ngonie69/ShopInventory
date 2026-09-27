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

    private static QuartzOptions Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddShopInventoryQuartz(configuration, "Host=unused");

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<QuartzOptions>>().Value;
    }
}
