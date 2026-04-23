using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public interface IMobileOrderPostProcessingQueue
{
    Task<List<MobileOrderPostProcessingQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 10,
        CancellationToken cancellationToken = default);

    Task MarkAsProcessingAsync(int queueId, CancellationToken cancellationToken = default);

    Task ResetStaleProcessingEntriesAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default);
}