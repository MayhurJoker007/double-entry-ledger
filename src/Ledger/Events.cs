using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ledger;

/// <summary>
/// The ledger is stored as the list of things that happened, never as balances. Balances, the trial balance and
/// the income statement are all derived by replaying these. Nothing is ever updated or deleted; a mistake is fixed
/// by posting a reversal, which is how accountants expect a ledger to behave anyway.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AccountOpened), "account-opened")]
[JsonDerivedType(typeof(EntryPosted), "entry-posted")]
[JsonDerivedType(typeof(EntryReversed), "entry-reversed")]
[JsonDerivedType(typeof(PeriodClosed), "period-closed")]
public abstract record LedgerEvent;

public sealed record AccountOpened(Account Account) : LedgerEvent;

public sealed record EntryPosted(JournalEntry Entry) : LedgerEvent;

public sealed record EntryReversed(Guid Original, Guid Reversal) : LedgerEvent;

/// <summary>Nothing can be posted on or before this date any more.</summary>
public sealed record PeriodClosed(DateOnly Through) : LedgerEvent;

public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(LedgerEvent e) => JsonSerializer.Serialize(e, Options);

    public static LedgerEvent Deserialize(string json) =>
        JsonSerializer.Deserialize<LedgerEvent>(json, Options) ?? throw new InvalidDataException("empty event");
}
