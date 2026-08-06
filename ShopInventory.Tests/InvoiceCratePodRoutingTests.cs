using System.Reflection;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.Crates.Commands.EnsureInvoiceCrateTransaction;
using ShopInventory.Features.Crates.Commands.UploadCratePod;
using ShopInventory.Features.Crates.Commands.UploadInvoiceCratePod;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The mobile app uploads crate PODs through POST /api/invoice/{docEntry}/crate-pod, which reaches
/// <see cref="UploadCratePodHandler"/> only by way of <see cref="UploadInvoiceCratePodHandler"/>.
/// That handler rebuilds the command, so anything it does not copy across is silently lost — and the
/// idempotency key is the one field where losing it leaves no guard at all: the handler consults its
/// durable store only when it has a key, and <see cref="ShopInventory.Middleware.IdempotencyMiddleware"/>
/// stands aside for this route on the understanding that the handler owns the replay. A dropped key
/// therefore disables both, which is what <see cref="CrateSubmissionIdempotencyTests"/> cannot see
/// because it exercises the inner handler directly.
/// </summary>
public sealed class InvoiceCratePodRoutingTests
{
    private static readonly Guid Driver = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task The_idempotency_key_reaches_the_inner_upload_command()
    {
        var mediator = new CapturingSender();

        await Handle(mediator, clientRequestId: "crate-key-1");

        Assert.Equal("crate-key-1", mediator.Captured!.ClientRequestId);
    }

    [Fact]
    public async Task A_caller_that_sends_no_key_still_reaches_the_inner_command_without_one()
    {
        // No key means no promise, exactly as on the crates route. An older app build must keep
        // working rather than start failing on a missing field.
        var mediator = new CapturingSender();

        await Handle(mediator, clientRequestId: null);

        Assert.Null(mediator.Captured!.ClientRequestId);
    }

    [Fact]
    public async Task The_rest_of_the_submission_survives_the_hop_intact()
    {
        // The command is rebuilt positionally, so a field inserted in the wrong place would compile
        // and quietly transpose two values.
        var mediator = new CapturingSender();

        await Handle(mediator, clientRequestId: "crate-key-2");

        var captured = mediator.Captured!;
        Assert.Equal(CrateTransactionId, captured.CrateTransactionId);
        Assert.Equal("Driver", captured.SubmissionRole);
        Assert.Equal(12m, captured.Quantity);
        Assert.Equal("left at gate", captured.Notes);
        Assert.Equal("CRATE_POD_5001_1.jpg", captured.FileName);
        Assert.Equal("image/jpeg", captured.ContentType);
        Assert.Equal(Driver, captured.UserId);
    }

    private const int CrateTransactionId = 77;

    private static async Task Handle(CapturingSender mediator, string? clientRequestId)
    {
        var handler = new UploadInvoiceCratePodHandler(
            NeverCalled<ISAPServiceLayerClient>(),
            mediator,
            NullLogger<UploadInvoiceCratePodHandler>.Instance);

        using var file = new MemoryStream([1, 2, 3]);
        var result = await handler.Handle(
            new UploadInvoiceCratePodCommand(
                InvoiceDocEntry: 9001,
                // Supplied, so the handler has no reason to reach for SAP.
                InvoiceDocNum: 5001,
                SubmissionRole: "Driver",
                Quantity: 12m,
                Notes: "left at gate",
                FileStream: file,
                FileName: "CRATE_POD_5001_1.jpg",
                ContentType: "image/jpeg",
                UserId: Driver,
                ClientRequestId: clientRequestId),
            CancellationToken.None);

        Assert.False(result.IsError);
    }

    /// <summary>
    /// Routes the two commands this handler sends and keeps the inner one for inspection. Any other
    /// request fails the test rather than returning a default that would hide a wiring change.
    /// </summary>
    private sealed class CapturingSender : ISender
    {
        public UploadCratePodCommand? Captured { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case EnsureInvoiceCrateTransactionCommand ensure:
                    return Task.FromResult((TResponse)(object)(ErrorOr.ErrorOr<EnsureInvoiceCrateTransactionResponseDto>)
                        new EnsureInvoiceCrateTransactionResponseDto
                        {
                            Id = CrateTransactionId,
                            InvoiceDocNum = ensure.InvoiceDocNum,
                            ExpectedQuantity = ensure.ExpectedQuantity ?? 0m
                        });

                case UploadCratePodCommand upload:
                    Captured = upload;
                    return Task.FromResult((TResponse)(object)(ErrorOr.ErrorOr<CratePodSubmissionDto>)
                        new CratePodSubmissionDto { Id = 1 });

                default:
                    throw new InvalidOperationException(
                        $"UploadInvoiceCratePodHandler must not send {request.GetType().Name}.");
            }
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A dependency the code under test must not touch. Generated rather than hand-stubbed because
    /// <see cref="ISAPServiceLayerClient"/> is far too wide to implement for one unused call.
    /// </summary>
    private static T NeverCalled<T>() where T : class
        => DispatchProxy.Create<T, ThrowingProxy>();

    public class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException(
                $"{targetMethod?.Name} should not be reached when the invoice doc num is already known.");
    }
}
