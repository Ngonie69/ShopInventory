using Quartz;

namespace ShopInventory.Web.Services;

/// <summary>
/// Registers the Web app's scheduled email jobs on a clustered, Postgres-backed Quartz scheduler.
/// Uses a distinct SchedulerName ("ShopInventoryWeb") so it clusters independently of the API's
/// scheduler even if they share a database (Quartz partitions all rows by sched_name). Clustering
/// ensures only one Web node sends each batch of statement / POD report emails.
/// </summary>
public static class WebQuartzConfiguration
{
    public static IServiceCollection AddShopInventoryWebQuartz(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddQuartz(q =>
        {
            q.SchedulerName = "ShopInventoryWeb";
            q.SchedulerId = "AUTO";

            q.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.UsePostgres(postgres => postgres.ConnectionString = connectionString);
                store.UseSystemTextJsonSerializer();
                store.UseClustering(cluster =>
                {
                    cluster.CheckinInterval = TimeSpan.FromSeconds(15);
                    cluster.CheckinMisfireThreshold = TimeSpan.FromSeconds(20);
                });
            });

            AddIntervalJob<StatementEmailJob>(q, "statement-emails", TimeSpan.FromMinutes(30));

            // Polled every minute because POD schedules are configured to the minute; the job
            // itself is a cheap no-op unless a schedule's send time has just elapsed.
            AddIntervalJob<PodReportEmailJob>(q, "pod-report-emails", TimeSpan.FromMinutes(1));
        });

        services.AddQuartzHostedService(options =>
        {
            options.WaitForJobsToComplete = true;
            options.AwaitApplicationStarted = true;
        });

        return services;
    }

    private static void AddIntervalJob<TJob>(
        IServiceCollectionQuartzConfigurator quartz,
        string name,
        TimeSpan interval)
        where TJob : IJob
    {
        var jobKey = new JobKey(name);
        quartz.AddJob<TJob>(job => job.WithIdentity(jobKey).StoreDurably());
        quartz.AddTrigger(trigger => trigger
            .ForJob(jobKey)
            .WithIdentity($"{name}-trigger")
            .StartNow()
            .WithSimpleSchedule(schedule => schedule
                .WithInterval(interval)
                .RepeatForever()
                .WithMisfireHandlingInstructionNextWithRemainingCount()));
    }
}
