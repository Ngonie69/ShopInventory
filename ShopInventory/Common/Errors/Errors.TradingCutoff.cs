using ErrorOr;
using ShopInventory.Common.Sales;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class TradingCutoff
    {
        /// <summary>
        /// The channel, or the account, has passed today's cut-off.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A Validation error, so a 400. The till shows the server's reason only for a 400 or a 422; a 409
        /// reaches the cashier as "it may or may not have been created". A 503 would be worse: the clients
        /// read it as the server being down and retry.
        /// </para>
        /// <para>
        /// The van handset rewrites any message holding one of its keywords ("timed out", "connection …",
        /// "duplicate", "insufficient"), so this wording keeps clear of all of them.
        /// <c>TradingCutoffTests</c> runs it through a copy of the handset's reader.
        /// </para>
        /// </remarks>
        /// <param name="channel">The channel the transaction arrived through.</param>
        /// <param name="closedAt">The CAT time trading closed at.</param>
        /// <param name="byAccount">True when the account's own exception closed it.</param>
        /// <param name="transaction">"sale" or "order".</param>
        public static Error Closed(string channel, TimeOnly closedAt, bool byAccount, string transaction)
        {
            var who = byAccount ? "this account" : SaleBusinesses.Label(channel).ToLowerInvariant();
            var at = closedAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            return Error.Validation(
                "TradingCutoff.Closed",
                $"Trading closed at {at} today for {who}, so this {transaction} cannot be taken. "
                + "Trading opens again tomorrow; ask the office if it must go through today.");
        }

        public static Error AccountsNotFound(IEnumerable<Guid> userIds) =>
            Error.NotFound(
                "TradingCutoff.AccountsNotFound",
                $"These accounts no longer exist: {string.Join(", ", userIds)}. Reload the page and save again.");
    }
}
