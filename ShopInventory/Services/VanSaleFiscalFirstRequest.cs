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
public sealed record VanSaleFiscalFirstRequest(
    string ReservationId,
    string? DocDate = null,
    string? DocDueDate = null,
    string? NumAtCard = null,
    string? Comments = null,
    decimal AmountPaid = 0m,
    bool MayAlreadyBeFiscalised = false);
