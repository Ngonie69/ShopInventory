using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;
using ShopInventory.Behaviors;
using ShopInventory.Common.Errors;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that every MediatR request leaves a record of how it ended, not just that it ran.
/// </summary>
/// <remarks>
/// A paged stock request in production spent exactly 60.000 seconds, returned an error, and left
/// nothing behind but <c>Handling GetStockInWarehousePagedQuery</c> and
/// <c>Handled GetStockInWarehousePagedQuery</c> — because the catch block that produced the error
/// had no logger call in it. <c>scripts/find_silent_catches.py</c> finds 58 catch blocks shaped
/// that way. Rather than hand-write a log line into each, the pipeline reports the outcome, which
/// covers all of them and every one written after this.
/// </remarks>
public sealed class LoggingBehaviorOutcomeTests
{
    public sealed record Request : IRequest<ErrorOr<string>>;

    private readonly CapturingLogger<LoggingBehavior<Request, ErrorOr<string>>> _log = new();

    [Fact]
    public async Task An_error_returned_by_the_handler_is_recorded_with_its_reason()
    {
        var error = Errors.Stock.SapError("Request was cancelled by client.");

        var result = await RunAsync(() => Task.FromResult<ErrorOr<string>>(error));

        Assert.True(result.IsError);

        var entry = Assert.Single(_log.AtOrAbove(LogLevel.Information));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(nameof(Request), entry.Message);
        Assert.Contains(error.Code, entry.Message);
        Assert.Contains("Request was cancelled by client.", entry.Message);
        Assert.Contains("ms", entry.Message);
    }

    /// <summary>
    /// A returned error is the handler answering — usually a validation or business refusal. It
    /// must not be promoted to Warning, or this repeats the mistake that made every error-level
    /// line in a production day a salesperson hitting a credit limit.
    /// </summary>
    [Fact]
    public async Task A_returned_error_is_not_promoted_to_a_warning()
    {
        await RunAsync(() => Task.FromResult<ErrorOr<string>>(Errors.Stock.SapDisabled));

        Assert.Empty(_log.AtOrAbove(LogLevel.Warning));
    }

    [Fact]
    public async Task A_fast_success_stays_at_debug()
    {
        var result = await RunAsync(() => Task.FromResult<ErrorOr<string>>("done"));

        Assert.False(result.IsError);
        Assert.Empty(_log.AtOrAbove(LogLevel.Information));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("completed in"));
    }

    [Fact]
    public async Task A_client_disconnect_is_recorded_with_its_duration_and_is_not_a_fault()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(() => throw new TaskCanceledException(), aborted.Token));

        var entry = Assert.Single(_log.AtOrAbove(LogLevel.Information));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("canceled by the caller", entry.Message);
        Assert.Contains("ms", entry.Message);
    }

    /// <summary>An exception the handler let escape is a different thing: it never answered.</summary>
    [Fact]
    public async Task An_escaping_exception_is_a_warning_and_keeps_its_stack()
    {
        var failure = new InvalidOperationException("SAP rejected the document.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(() => throw failure));

        var entry = Assert.Single(_log.AtOrAbove(LogLevel.Warning));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains("failed after", entry.Message);
    }

    /// <summary>
    /// The behavior is registered open-generically, so it also wraps handlers whose response is not
    /// an <see cref="IErrorOr"/>. Those must still be reported rather than throwing on the cast.
    /// </summary>
    [Fact]
    public async Task A_response_that_is_not_an_ErrorOr_is_still_reported()
    {
        var log = new CapturingLogger<LoggingBehavior<Request, int>>();
        var behavior = new LoggingBehavior<Request, int>(log);

        var result = await behavior.Handle(new Request(), _ => Task.FromResult(42), CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Contains(log.Entries, e => e.Message.Contains("completed in"));
    }

    private Task<ErrorOr<string>> RunAsync(
        Func<Task<ErrorOr<string>>> handler,
        CancellationToken cancellationToken = default)
        => new LoggingBehavior<Request, ErrorOr<string>>(_log)
            .Handle(new Request(), _ => handler(), cancellationToken);
}
