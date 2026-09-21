using MediatR;

namespace ShopInventory.Tests;

/// <summary>
/// An <see cref="IPublisher"/> that keeps what it was given, for a test that has to see which events a
/// handler raised and in what order — and, as often, that it raised none.
/// </summary>
internal sealed class CapturingPublisher : IPublisher
{
    private readonly List<object> _published = [];

    public IReadOnlyList<object> Published => _published;

    public IReadOnlyList<T> Of<T>() => _published.OfType<T>().ToList();

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        _published.Add(notification);
        return Task.CompletedTask;
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        _published.Add(notification!);
        return Task.CompletedTask;
    }
}
