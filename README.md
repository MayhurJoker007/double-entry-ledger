# Double-entry ledger

![CI](https://github.com/MayhurJoker007/double-entry-ledger/actions/workflows/ci.yml/badge.svg)

A general ledger in C# built on **event sourcing**: the database stores what happened (account opened, entry
posted, entry reversed, period closed), never balances. Balances, the trial balance and the income statement are
all worked out by replaying those events.

Accounting is a natural fit for this. Accountants already work this way: you never rub out a posted entry,
you post a reversal, and the full history is the point.

**Stack:** .NET 9, SQLite (plain ADO.NET), System.Text.Json polymorphic serialisation, xUnit.

```csharp
var store = new SqliteEventStore("Data Source=ledger.db");
var books = GeneralLedger.Load(store, "demo");

books.Post(new DateOnly(2026, 3, 9), "Invoice INV-1001, Soweto Sports Tavern",
    [Dr("1100", 28_750m), Cr("4000", 25_000m), Cr("2100", 3_750m)],
    reference: "INV-1001");
books.Save(store);
```

```
Trial balance at 31 March 2026
  1000  Bank                   62 909,45
  1200  Stock                  17 300,00
  2100  VAT                                   3 750,00
  3000  Owner's capital                      80 000,00
  4000  Sales                                25 000,00
  5000  Cost of sales          17 200,00
  6100  Rent                    9 000,00
  6200  Fuel                    2 340,55
                              108 750,00    108 750,00  balanced
```

## The rules it enforces

- **Every entry balances**: debits equal credits, to the cent. There are at least two lines, each line is
  either a debit or a credit (never both, never negative), and amounts can't go below a cent.
- **Posted means posted.** Corrections are reversals: the mirror-image entry is posted and linked to the
  original, and both stay on record. A reversal can't itself be reversed, and nothing can be reversed twice.
- **Closed periods are closed.** After `ClosePeriod(31 March)`, nothing can be dated on or before it.
- **Idempotent by reference.** Posting `INV-1001` again returns the original entry. Run the demo twice and
  the figures don't double, because a retried import doesn't double-count.
- **Append-only, enforced twice.** The code never updates or deletes events, and SQLite triggers refuse
  `UPDATE` and `DELETE` on the events table anyway.
- **Optimistic concurrency.** Each ledger is a stream with a version. If two people load version 12 and both try
  to save, the second gets a `ConcurrencyException` (a version check plus a unique `(stream, version)` index)
  and has to reload and try again, so nothing is silently overwritten.

## Reports

- `Balance(account, asOf)` shows the balance in the account's normal direction: an asset's debit balance and an
  income account's credit balance are both positive.
- `TrialBalance(asOf)` lists debits and credits per account, and whether they balance (they always should,
  and a test checks it).
- `IncomeStatement(from, to)` gives income, expenses and net profit for the period.

Because the history is all there, "what was the bank balance on 14 March?" is just a replay up to that date.

## Running

```
dotnet run --project src/Ledger.Demo
dotnet test
```

The tests cover normal-direction balances, each kind of invalid entry, reversals (including double
reversals), reference idempotency, closed periods, two writers saving at once, trial balance and income
statement figures, and the append-only triggers.
