using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPodFile;

/// <summary>
/// One page of a delivery note, sent as a file rather than as base64 inside a JSON body.
/// </summary>
/// <remarks>
/// The van sales half of <c>InvoiceController.UploadPod</c>, which is the route the drivers' POD app
/// uses. It exists separately because that one is gated <c>[Authorize(Roles = "...,SalesRep")]</c> and
/// a van rep's role is <c>Sales</c> — a different role — so every van upload would be refused there.
/// Widening that list would open a route the drivers' app depends on to two roles that have no other
/// business on it; this keeps van authorisation where the rest of the van routes keep it.
///
/// <para>Everything past the gate is the same command the portal and the drivers' app reach, so a POD
/// filed from a van is stored, deduplicated, audited and announced identically to any other.</para>
/// </remarks>
/// <param name="Order">The platform order or invoice id the handset holds. Resolved to a SAP document.</param>
/// <param name="FileStream">The page itself, at whatever size the handset encoded it to.</param>
/// <param name="FileName">What the page is stored as. Prefixed <c>POD_</c> downstream if it is not already.</param>
/// <param name="ContentType">The page's media type, which decides how the office is served it back.</param>
/// <param name="Description">Free text filed with the attachment. The handset sends a fixed label.</param>
/// <param name="ExternalReference">
/// The caller's own idempotency key for this page. Passed through untouched.
/// </param>
/// <param name="IsAdditionalPage">
/// Set by the caller for every page after the first of one note. Without it the double-submit window
/// inside <c>UploadPodCommand</c> reads pages sent moments apart as the same page arriving twice.
/// </param>
/// <param name="UserId">The signed-in account, which owns the attachment and is who it is announced for.</param>
public sealed record UploadVanSalesPodFileCommand(
    int Order,
    Stream FileStream,
    string FileName,
    string ContentType,
    string? Description,
    string? ExternalReference,
    bool IsAdditionalPage,
    Guid UserId
) : IRequest<ErrorOr<DocumentAttachmentDto>>;
