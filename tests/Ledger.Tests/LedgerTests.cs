using Ledger;
using static Ledger.EntryLine;

namespace Ledger.Tests;

public class LedgerTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}.db");
    private readonly SqliteEventStore _store;

    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar15 = new(2026, 3, 15);
    private static readonly DateOnly Apr2 = new(2026, 4, 2);

    public LedgerTests()
    {
        _store = new SqliteEventStore($"Data Source={_db};Pooling=False");
        var ledger = GeneralLedger.Load(_store, "shop");
        ledger.OpenAccount("1000", "Bank", AccountType.Asset);
        ledger.OpenAccount("1100", "Debtors", AccountType.Asset);
        ledger.OpenAccount("1200", "Stock", AccountType.Asset);
        ledger.OpenAccount("2100", "VAT output", AccountType.Liability);
        ledger.OpenAccount("3000", "Owner's capital", AccountType.Equity);
        ledger.OpenAccount("4000", "Sales", AccountType.Income);
        ledger.OpenAccount("5000", "Cost of sales", AccountType.Expense);
        ledger.OpenAccount("6100", "Rent", AccountType.Expense);
        ledger.Save(_store);
    }

    private GeneralLedger Load() => GeneralLedger.Load(_store, "shop");

    [Fact]
    public void Balanced_entries_update_balances_in_their_normal_direction()
    {
        var l = Load();
        l.Post(Mar1, "Owner puts money in", [Dr("1000", 50_000m), Cr("3000", 50_000m)]);
        l.Post(Mar15, "Cash sale incl. VAT", [Dr("1000", 1_150m), Cr("4000", 1_000m), Cr("2100", 150m)]);
        l.Save(_store);

        var reloaded = Load();
        Assert.Equal(51_150m, reloaded.Balance("1000"));
        Assert.Equal(1_000m, reloaded.Balance("4000"));   // income shows positive, not as a negative debit
        Assert.Equal(150m, reloaded.Balance("2100"));
    }

    [Theory]
    [MemberData(nameof(BadEntries))]
    public void Rejects_invalid_entries(EntryLine[] lines, string expected)
    {
        var l = Load();
        var e = Assert.Throws<LedgerException>(() => l.Post(Mar1, "bad", lines));
        Assert.Contains(expected, e.Message);
    }

    public static TheoryData<EntryLine[], string> BadEntries => new()
    {
        { [Dr("1000", 100m), Cr("4000", 99.99m)], "doesn't balance" },
        { [Dr("1000", 100m)], "at least two lines" },
        { [Dr("1000", 100m), Cr("9999", 100m)], "Unknown account 9999" },
        { [Dr("1000", -100m), Cr("4000", -100m)], "can't be negative" },
        { [new EntryLine("1000", 100m, 100m), Cr("4000", 0m)], "either a debit or a credit" },
        { [Dr("1000", 10.005m), Cr("4000", 10.005m)], "cents" },
    };

    [Fact]
    public void Reversal_cancels_the_entry_but_keeps_both_on_record()
    {
        var l = Load();
        var rent = l.Post(Mar1, "Rent March", [Dr("6100", 8_500m), Cr("1000", 8_500m)]);
        var reversal = l.Reverse(rent, Mar15, "posted twice by mistake");
        l.Save(_store);

        var reloaded = Load();
        Assert.Equal(0m, reloaded.Balance("6100"));
        Assert.True(reloaded.IsReversed(rent));
        Assert.Equal(2, reloaded.Entries.Count());
        Assert.Throws<LedgerException>(() => reloaded.Reverse(rent, Mar15, "again"));
        Assert.Throws<LedgerException>(() => reloaded.Reverse(reversal, Mar15, "undo the undo"));
    }

    [Fact]
    public void Same_reference_is_only_posted_once()
    {
        var l = Load();
        var first = l.Post(Mar1, "Invoice INV-1001", [Dr("1100", 2_300m), Cr("4000", 2_000m), Cr("2100", 300m)], "INV-1001");
        var again = l.Post(Mar1, "Invoice INV-1001", [Dr("1100", 2_300m), Cr("4000", 2_000m), Cr("2100", 300m)], "INV-1001");
        Assert.Equal(first, again);
        Assert.Equal(2_300m, l.Balance("1100"));
    }

    [Fact]
    public void Closed_periods_cannot_be_posted_into()
    {
        var l = Load();
        l.Post(Mar15, "Sale", [Dr("1000", 500m), Cr("4000", 500m)]);
        l.ClosePeriod(new DateOnly(2026, 3, 31));
        l.Save(_store);

        var reloaded = Load();
        var e = Assert.Throws<LedgerException>(() => reloaded.Post(Mar15, "Late", [Dr("1000", 1m), Cr("4000", 1m)]));
        Assert.Contains("closed", e.Message);
        reloaded.Post(Apr2, "April sale", [Dr("1000", 1m), Cr("4000", 1m)]);   // the next period is fine
    }

    [Fact]
    public void Concurrent_writers_cannot_both_append()
    {
        var a = Load();
        var b = Load();
        a.Post(Mar1, "From A", [Dr("1000", 10m), Cr("4000", 10m)]);
        b.Post(Mar1, "From B", [Dr("1000", 20m), Cr("4000", 20m)]);

        a.Save(_store);
        Assert.Throws<ConcurrencyException>(() => b.Save(_store));
        Assert.Equal(10m, Load().Balance("1000"));
    }

    [Fact]
    public void Trial_balance_always_balances_and_income_statement_adds_up()
    {
        var l = Load();
        l.Post(Mar1, "Capital", [Dr("1000", 50_000m), Cr("3000", 50_000m)]);
        l.Post(Mar1, "Buy stock", [Dr("1200", 12_000m), Cr("1000", 12_000m)]);
        l.Post(Mar15, "Sale on account", [Dr("1100", 23_000m), Cr("4000", 20_000m), Cr("2100", 3_000m)]);
        l.Post(Mar15, "Cost of that sale", [Dr("5000", 9_000m), Cr("1200", 9_000m)]);
        l.Post(Mar15, "Rent", [Dr("6100", 8_500m), Cr("1000", 8_500m)]);
        l.Post(Apr2, "April sale", [Dr("1000", 1_150m), Cr("4000", 1_000m), Cr("2100", 150m)]);

        var tb = l.TrialBalance(new DateOnly(2026, 3, 31));
        Assert.True(tb.Balances);
        Assert.Equal(73_000m, tb.TotalDebits);   // bank 29 500 + debtors 23 000 + stock 3 000 + cost of sales 9 000 + rent 8 500

        var march = l.IncomeStatement(Mar1, new DateOnly(2026, 3, 31));
        Assert.Equal(20_000m, march.Income);
        Assert.Equal(17_500m, march.Expenses);
        Assert.Equal(2_500m, march.NetProfit);
        Assert.Equal(29_500m, l.Balance("1000", new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public void Events_cannot_be_edited_or_deleted()
    {
        Load().Save(_store);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM events";
        var e = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("append-only", e.Message);
    }

    public void Dispose() => File.Delete(_db);
}
