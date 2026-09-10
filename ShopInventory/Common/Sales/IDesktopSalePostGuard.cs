using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Lets exactly one post per sale be in flight at a time.
/// </summary>
/// <remarks>
/// <para>
/// The sale's own guards answer a different question. <c>PostIssuedAtUtc</c> and the
/// <c>U_Van_saleorder</c> lookup make a <i>sequence</i> of attempts safe: ask SAP, mark before
/// sending, adopt whatever is found. They say nothing about two attempts running at once, and both
/// would pass their checks before either wrote anything.
/// </para>
/// <para>
/// That was tolerable while the only writer was one background job. It stops being tolerable the
/// moment a person can press "Post to SAP" — the job fires every minute, so a manual post lands
/// inside a pass roughly whenever it is pressed, and each of the two would find no invoice and send
/// one. So the guard lives here rather than in either caller: this is the single point where the
/// right to create a SAP document is handed out, and the job, the manual post and the bulk post
/// therefore exclude each other rather than only themselves.
/// </para>
/// <para>
/// Durable and cross-instance, because <c>IIdempotencyRequestStore</c> is a Postgres table. The
/// deployment is clustered, so an in-process lock would guard one node and leave the others free.
/// </para>
/// </remarks>
public interface IDesktopSalePostGuard
{
    /// <summary>
    /// Asks for the right to post <paramref name="sale"/>. Dispose the claim to give it back;
    /// complete it once SAP holds the invoice.
    /// </summary>
    Task<DesktopSalePostClaim> ClaimAsync(DesktopSaleEntity sale, CancellationToken cancellationToken);
}
