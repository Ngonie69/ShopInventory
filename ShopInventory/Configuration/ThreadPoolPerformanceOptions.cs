namespace ShopInventory.Configuration;

public sealed class ThreadPoolPerformanceOptions
{
    public const string SectionName = "Performance:ThreadPool";

    public int MinWorkerThreads { get; set; }

    public int MinCompletionPortThreads { get; set; }
}