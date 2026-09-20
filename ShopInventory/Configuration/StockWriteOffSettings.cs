namespace ShopInventory.Configuration;

/// <summary>
/// Settings for stock write-offs: the goods issue that takes counted stock off SAP's books.
/// </summary>
public sealed class StockWriteOffSettings
{
    public const string SectionName = "StockWriteOffs";

    /// <summary>
    /// The reasons offered when SAP itself defines none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A user field is defined per table in Business One, so the company database may carry no reason
    /// field on the goods-issue line table at all. Where it does, SAP's own valid values are used and
    /// this list is ignored — SAP rejects any value its field does not define, so the picker and the
    /// payload have to come from the same place. This list is the fallback for the other case, where
    /// the reason can only be recorded here and in the document's comments.
    /// </para>
    /// <para>
    /// Deliberately empty rather than carrying the defaults here: the configuration binder appends to
    /// a collection that is already populated, so an initializer plus the same list in
    /// appsettings.json binds to both. <c>OptionsCollectionBindingTests</c> pins that. The shipped
    /// list lives in appsettings.json, which is also where a company that wants different reasons
    /// changes them.
    /// </para>
    /// </remarks>
    public List<string> Reasons { get; set; } = [];

    /// <summary>
    /// What goes in the SAP document's <c>JournalMemo</c>, which is what shows on the journal entry.
    /// </summary>
    public string JournalMemo { get; set; } = "Stock write-off";
}
