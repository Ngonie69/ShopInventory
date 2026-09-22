namespace ShopInventory.Services;

/// <summary>
/// One van sale to fiscalise and then post, identified by the reservation holding its stock.
/// </summary>
/// <param name="ReservationId">The reservation the invoice posts from.</param>
/// <param name="DocDate">The invoice date, <c>yyyy-MM-dd</c>. Null posts today's.</param>
/// <param name="DocDueDate">The due date, <c>yyyy-MM-dd</c>.</param>
/// <param name="NumAtCard">SAP's customer reference. Null lets the reservation derive it.</param>
/// <param name="Comments">The invoice remarks.</param>
/// <param name="AmountPaid">Money the business kept, net of change. Never negative.</param>
/// <param name="MayAlreadyBeFiscalised">
/// Whether an earlier attempt may have reached the device without leaving a row here — a queue entry
/// that was fiscalised before this path existed, for one. The device is then asked for an existing receipt
/// before anything is sent, because a duplicate cannot be withdrawn.
/// </param>
/// <param name="PostToSapNow">
/// Whether SAP is asked for the invoice in this call. False signs and stops: the sale comes back
/// <see cref="VanSaleFiscalFirstStatus.AwaitingSap"/> with <see cref="VanSaleFiscalFirstOutcome.Deferred"/>
/// set, for the caller to hand to the invoice queue. The handset route does this so a rep waits for the
/// fiscal device and not for SAP as well; a person pressing Post on the console wants the post.
/// </param>
public sealed record VanSaleFiscalFirstRequest(
    string ReservationId,
    string? DocDate = null,
    string? DocDueDate = null,
    string? NumAtCard = null,
    string? Comments = null,
    decimal AmountPaid = 0m,
    bool MayAlreadyBeFiscalised = false,
    bool PostToSapNow = true);
