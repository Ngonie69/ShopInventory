using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ShopInventory.Web.Data;

/// <summary>
/// Makes every instant column take UTC, whatever <see cref="DateTimeKind"/> reaches it.
/// </summary>
/// <remarks>
/// Deliberately a copy of the API's class of the same name, because these two applications share no
/// project. Both databases are PostgreSQL and both hit the same Npgsql rule, so they must not drift:
/// change one and change the other.
/// </remarks>
/// <remarks>
/// Npgsql refuses to write a <see cref="DateTime"/> to <c>timestamp with time zone</c> unless its Kind
/// is Utc — it throws rather than converting, and it throws for query parameters exactly as it does for
/// stored values. So a date bound from a query string (Kind=Unspecified) or a <c>DateTime.Today</c>
/// (Kind=Local) compared against such a column does not return the wrong rows: the whole query fails.
/// Two of those shipped, one of them silently emptying half the activity log, and neither was findable
/// by test — the SQLite suite compares the two Kinds happily, so this class of bug reaches production
/// through a green build every time.
///
/// This converter closes the class. Attaching it to the property makes EF apply it to parameters
/// compared against that property as well as to values written to it, which is the half that matters:
/// most of these sites never write a DateTime at all, they only filter on one.
///
/// <para><b>Unspecified is stamped, not converted.</b> Local carries an offset and converts to the
/// instant it names. Unspecified carries nothing, and the choice is between reading it as UTC or as the
/// server's local time. It is read as UTC, matching this codebase's own rule that timestamps are stored
/// and reasoned about in UTC: reading it as local would silently move every bare <c>new DateTime(…)</c>
/// and every parsed date by the CAT offset, which is a two-hour error in results nobody would see.
/// A caller whose dates really do mean the reader's own day — an operator asking for "yesterday" —
/// must convert before handing them over, because only that caller knows it. <c>AuditService</c> does
/// exactly that and this convention deliberately leaves its already-UTC values alone.</para>
///
/// <para><b>What it does not touch.</b> Columns that are not an instant: a <c>date</c> is a calendar
/// day, and converting a local midnight to UTC would move it to the previous day — a data bug strictly
/// worse than the one being fixed. Properties that already carry an explicit converter are left to it.
/// On the read side this is a no-op in production: Npgsql already returns Kind=Utc for a timestamptz,
/// so nothing that renders a timestamp changes behaviour.</para>
/// </remarks>
internal static class UtcDateTimeConvention
{
    /// <summary>
    /// Column types that name a calendar day or a wall clock rather than an instant.
    /// </summary>
    /// <remarks>
    /// Matched against the configured type, so it reads the same under Npgsql and under the SQLite the
    /// tests use. A DateTime property with no explicit type is an instant on both.
    /// </remarks>
    private static readonly HashSet<string> NotAnInstant = new(StringComparer.OrdinalIgnoreCase)
    {
        "date",
        "timestamp without time zone",
        "time",
        "time without time zone",
        "time with time zone",
        "timetz",
        "interval"
    };

    private static readonly ValueConverter<DateTime, DateTime> ToUtc = new(
        value => value.Kind == DateTimeKind.Utc
            ? value
            : value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> ToUtcNullable = new(
        value => value == null
            ? null
            : value.Value.Kind == DateTimeKind.Utc
                ? value
                : value.Value.Kind == DateTimeKind.Local
                    ? value.Value.ToUniversalTime()
                    : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        value => value == null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    /// <summary>
    /// Attaches the converter to every instant-typed <see cref="DateTime"/> property in the model.
    /// </summary>
    /// <remarks>
    /// Call last in <c>OnModelCreating</c>. It reads each property's configured column type to decide,
    /// so anything that sets one — <c>HasColumnType</c>, <c>[Column(TypeName = …)]</c> — has to be in
    /// place before this runs.
    /// </remarks>
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var isDateTime = property.ClrType == typeof(DateTime);
                var isNullableDateTime = property.ClrType == typeof(DateTime?);

                if (!isDateTime && !isNullableDateTime)
                {
                    continue;
                }

                // Something configured this property deliberately. Overriding it here would be a
                // convention quietly beating an explicit decision, which is the wrong way round.
                if (property.GetValueConverter() is not null)
                {
                    continue;
                }

                var columnType = property.GetColumnType();

                if (columnType is not null && NotAnInstant.Contains(columnType))
                {
                    continue;
                }

                property.SetValueConverter(isDateTime ? ToUtc : ToUtcNullable);
            }
        }
    }
}
