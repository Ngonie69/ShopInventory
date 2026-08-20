using Microsoft.Extensions.Logging;

namespace ShopInventory.Tests;

/// <summary>
/// An <see cref="ILogger"/> that records what was written, so a test can assert on the level a
/// message was logged at rather than only on what it said.
/// </summary>
/// <remarks>
/// Level is behaviour, not decoration: <c>[ERR]</c> is what an operator builds an alert on, so a
/// business outcome logged at Error is a defect even though the text is correct. Tests that pin a
/// level need somewhere to read it back from, and <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/>
/// discards it.
/// </remarks>
public class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));

    /// <summary>Every entry at <paramref name="level"/> or above.</summary>
    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> AtOrAbove(LogLevel level)
        => Entries.Where(entry => entry.Level >= level).ToList();
}

/// <summary>
/// <see cref="CapturingLogger"/> for a service that takes <see cref="ILogger{TCategoryName}"/>.
/// </summary>
public sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;
