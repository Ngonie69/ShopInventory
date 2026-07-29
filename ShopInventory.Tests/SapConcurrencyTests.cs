using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// <see cref="SAPConcurrencyHandler"/> holds its semaphores in statics shared by the whole
/// process, so these tests must not run alongside each other. Each also uses a distinct limit
/// pair, which is what makes the handler rebuild them rather than inherit another test's.
/// </summary>
[CollectionDefinition("SapConcurrency", DisableParallelization = true)]
public sealed class SapConcurrencyCollection;

[Collection("SapConcurrency")]
public class SapRequestPriorityTests
{
    [Fact]
    public void Requests_are_background_by_default()
    {
        Assert.False(SapRequestPriority.IsInteractive);
    }

    [Fact]
    public void Scope_marks_the_current_flow_interactive_and_restores_it()
    {
        using (SapRequestPriority.BeginInteractive())
        {
            Assert.True(SapRequestPriority.IsInteractive);
        }

        Assert.False(SapRequestPriority.IsInteractive);
    }

    [Fact]
    public void Nested_scopes_do_not_end_the_outer_one()
    {
        using (SapRequestPriority.BeginInteractive())
        {
            using (SapRequestPriority.BeginInteractive())
            {
                Assert.True(SapRequestPriority.IsInteractive);
            }

            // A helper that opens its own scope must not un-prioritise the approval that called it.
            Assert.True(SapRequestPriority.IsInteractive);
        }

        Assert.False(SapRequestPriority.IsInteractive);
    }

    [Fact]
    public async Task Scope_flows_across_awaits_into_callees()
    {
        using (SapRequestPriority.BeginInteractive())
        {
            await Task.Yield();
            Assert.True(await Task.Run(() => SapRequestPriority.IsInteractive));
        }

        Assert.False(SapRequestPriority.IsInteractive);
    }
}

[Collection("SapConcurrency")]
public class SapConcurrencyHandlerTests
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Interactive_request_gets_a_slot_while_background_traffic_is_saturated()
    {
        // 4 slots, 1 reserved => background may hold at most 3.
        var gate = new BlockingHandler();
        using var invoker = CreateInvoker(gate, maxConcurrentRequests: 4, interactiveReservedRequests: 1);

        var background = Enumerable.Range(0, 12)
            .Select(_ => SendAsync(invoker, interactive: false))
            .ToArray();

        // Let the background flood take everything it is allowed to take and queue the rest.
        await gate.WaitForInFlightAsync(3, Timeout);
        await Task.Delay(SettleDelay);

        var interactive = SendAsync(invoker, interactive: true);

        // The point of the reservation: this must not wait for the twelve-deep background queue.
        await gate.WaitForInFlightAsync(4, Timeout);

        gate.ReleaseAll();
        await interactive.WaitAsync(Timeout);
        await Task.WhenAll(background).WaitAsync(Timeout);
    }

    [Fact]
    public async Task Background_requests_never_occupy_every_slot()
    {
        // 5 slots, 2 reserved => background may hold at most 3.
        var gate = new BlockingHandler();
        using var invoker = CreateInvoker(gate, maxConcurrentRequests: 5, interactiveReservedRequests: 2);

        var background = Enumerable.Range(0, 10)
            .Select(_ => SendAsync(invoker, interactive: false))
            .ToArray();

        await gate.WaitForInFlightAsync(3, Timeout);
        await Task.Delay(SettleDelay);

        Assert.Equal(3, gate.PeakInFlight);

        gate.ReleaseAll();
        await Task.WhenAll(background).WaitAsync(Timeout);
    }

    [Fact]
    public async Task Reserving_every_slot_still_leaves_background_work_able_to_run()
    {
        // A misconfigured reservation must not deadlock the queue consumers: 3 slots with 99
        // reserved still has to leave background work at least one.
        var gate = new BlockingHandler();
        using var invoker = CreateInvoker(gate, maxConcurrentRequests: 3, interactiveReservedRequests: 99);

        var background = SendAsync(invoker, interactive: false);

        await gate.WaitForInFlightAsync(1, Timeout);

        gate.ReleaseAll();
        await background.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Background_permit_is_returned_when_a_request_is_cancelled_while_queued()
    {
        // A background request takes its background permit before it queues for a slot, so a
        // cancellation between the two leaks the permit unless it is handed back. 6 slots, 2
        // reserved => background cap of 4; four interactive requests hold slots so that a fifth
        // background request queues for a slot rather than for a background permit.
        var gate = new BlockingHandler();
        using var invoker = CreateInvoker(gate, maxConcurrentRequests: 6, interactiveReservedRequests: 2);

        var interactive = Enumerable.Range(0, 4)
            .Select(_ => SendAsync(invoker, interactive: true))
            .ToArray();
        var background = Enumerable.Range(0, 2)
            .Select(_ => SendAsync(invoker, interactive: false))
            .ToArray();
        await gate.WaitForInFlightAsync(6, Timeout);

        // Holds a background permit (2 of 4 are in use) but cannot get one of the six slots.
        using var cancellation = new CancellationTokenSource();
        var queued = SendAsync(invoker, interactive: false, cancellation.Token);
        await Task.Delay(SettleDelay);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));

        gate.ReleaseAll();
        await Task.WhenAll(interactive.Concat(background)).WaitAsync(Timeout);

        // All four background permits have to be available again, not three.
        gate.Reset();
        var afterCancellation = Enumerable.Range(0, 4)
            .Select(_ => SendAsync(invoker, interactive: false))
            .ToArray();
        await gate.WaitForInFlightAsync(4, Timeout);

        gate.ReleaseAll();
        await Task.WhenAll(afterCancellation).WaitAsync(Timeout);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpMessageInvoker invoker,
        bool interactive,
        CancellationToken cancellationToken = default)
    {
        // Started on the pool so the ambient scope is captured for this request only, the way a
        // real request picks it up from the handler that opened it.
        return Task.Run(
            () =>
            {
                using var scope = interactive ? SapRequestPriority.BeginInteractive() : null;
                return invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://sap.invalid/b1s/v1/Orders"), cancellationToken);
            },
            CancellationToken.None);
    }

    private static HttpMessageInvoker CreateInvoker(
        HttpMessageHandler inner,
        int maxConcurrentRequests,
        int interactiveReservedRequests)
    {
        var handler = new SAPConcurrencyHandler(
            Options.Create(new SAPSettings
            {
                MaxConcurrentRequests = maxConcurrentRequests,
                InteractiveReservedRequests = interactiveReservedRequests
            }),
            NullLogger<SAPConcurrencyHandler>.Instance)
        {
            InnerHandler = inner
        };

        return new HttpMessageInvoker(handler);
    }

    /// <summary>
    /// Stands in for SAP: records how many requests are inside it at once and holds them there
    /// until released, so the test can observe exactly how many slots each priority is granted.
    /// </summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly Lock _sync = new();
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;

        public int PeakInFlight { get; private set; }

        public void ReleaseAll()
        {
            lock (_sync)
            {
                _release.TrySetResult();
            }
        }

        /// <summary>Re-arms the gate so a second wave of requests is held again.</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                PeakInFlight = 0;
            }
        }

        public async Task WaitForInFlightAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_sync)
                {
                    if (_inFlight >= expected)
                    {
                        return;
                    }
                }

                await Task.Delay(20);
            }

            lock (_sync)
            {
                Assert.Fail($"Expected at least {expected} concurrent request(s) but saw {_inFlight}.");
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Task release;
            lock (_sync)
            {
                _inFlight++;
                PeakInFlight = Math.Max(PeakInFlight, _inFlight);
                release = _release.Task;
            }

            try
            {
                await release.WaitAsync(cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
            finally
            {
                lock (_sync)
                {
                    _inFlight--;
                }
            }
        }
    }
}
