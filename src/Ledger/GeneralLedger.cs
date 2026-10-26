namespace Ledger;

/// <summary>
/// One ledger, rebuilt from its events. Commands check the rules against the current state, then append new events;
/// queries read the state. Load, change, save: if someone else saved in between, Save throws ConcurrencyException.
/// </summary>
public sealed class GeneralLedger
{
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, JournalEntry> _entries = new();
    private readonly Dictionary<string, Guid> _byReference = new();
    private readonly Dictionary<Guid, Guid> _reversedBy = new();
    private readonly List<LedgerEvent> _pending = [];
    private readonly string _stream;
    private long _version;

    public DateOnly? ClosedThrough { get; private set; }
    public IReadOnlyCollection<Account> Accounts => _accounts.Values;
    public IEnumerable<JournalEntry> Entries => _entries.Values.OrderBy(e => e.Date).ThenBy(e => e.Id);

    private GeneralLedger(string name) => _stream = "ledger-" + name;

    public static GeneralLedger Load(IEventStore store, string name)
    {
        var ledger = new GeneralLedger(name);
        foreach (var stored in store.Read(ledger._stream))
        {
            ledger.Apply(stored.Event);
            ledger._version = stored.Version;
        }
        return ledger;
    }

    public void Save(IEventStore store)
    {
        store.Append(_stream, _version, _pending);
        _version += _pending.Count;
        _pending.Clear();
    }

    // ---- commands -----------------------------------------------------------

    public void OpenAccount(string code, string name, AccountType type)
    {
        if (_accounts.ContainsKey(code)) throw new LedgerException($"Account {code} already exists");
        Raise(new AccountOpened(new Account(code, name, type)));
    }

    /// <summary>
    /// Posts a balanced entry. If an entry with the same reference was already posted (a retried import, say),
    /// returns that entry's id instead of posting it twice.
    /// </summary>
    public Guid Post(DateOnly date, string description, IReadOnlyList<EntryLine> lines, string? reference = null)
    {
        if (reference is not null && _byReference.TryGetValue(reference, out var existing)) return existing;
        Validate(date, lines);
        var entry = new JournalEntry(Guid.NewGuid(), date, description, reference, lines);
        Raise(new EntryPosted(entry));
        return entry.Id;
    }

    /// <summary>Undoes an entry by posting its mirror image. The original stays on record.</summary>
    public Guid Reverse(Guid entryId, DateOnly date, string reason)
    {
        if (!_entries.TryGetValue(entryId, out var original)) throw new LedgerException($"Entry {entryId} not found");
        if (original.Reverses is not null) throw new LedgerException("A reversal can't itself be reversed; post a new entry");
        if (_reversedBy.ContainsKey(entryId)) throw new LedgerException($"Entry {entryId} was already reversed");

        var lines = original.Lines.Select(l => new EntryLine(l.Account, l.Credit, l.Debit, l.Memo)).ToList();
        Validate(date, lines);
        var reversal = new JournalEntry(Guid.NewGuid(), date, $"Reversal: {reason}", null, lines, entryId);
        Raise(new EntryPosted(reversal));
        Raise(new EntryReversed(entryId, reversal.Id));
        return reversal.Id;
    }

    public void ClosePeriod(DateOnly through)
    {
        if (ClosedThrough is { } closed && through <= closed)
            throw new LedgerException($"Already closed through {closed:yyyy-MM-dd}");
        Raise(new PeriodClosed(through));
    }

    private void Validate(DateOnly date, IReadOnlyList<EntryLine> lines)
    {
        if (ClosedThrough is { } closed && date <= closed)
            throw new LedgerException($"The period up to {closed:yyyy-MM-dd} is closed");
        if (lines.Count < 2) throw new LedgerException("An entry needs at least two lines");

        foreach (var l in lines)
        {
            if (!_accounts.ContainsKey(l.Account)) throw new LedgerException($"Unknown account {l.Account}");
            if (l.Debit < 0 || l.Credit < 0) throw new LedgerException("Amounts can't be negative; swap debit and credit instead");
            if ((l.Debit > 0) == (l.Credit > 0)) throw new LedgerException($"Line for {l.Account} must have either a debit or a credit");
            if (decimal.Round(l.Debit, 2) != l.Debit || decimal.Round(l.Credit, 2) != l.Credit)
                throw new LedgerException("Amounts are limited to cents");
        }

        var debits = lines.Sum(l => l.Debit);
        var credits = lines.Sum(l => l.Credit);
        if (debits != credits)
            throw new LedgerException($"Entry doesn't balance: debits {debits:0.00}, credits {credits:0.00}");
    }

    private void Raise(LedgerEvent e)
    {
        Apply(e);
        _pending.Add(e);
    }

    private void Apply(LedgerEvent e)
    {
        switch (e)
        {
            case AccountOpened a:
                _accounts[a.Account.Code] = a.Account;
                break;
            case EntryPosted p:
                _entries[p.Entry.Id] = p.Entry;
                if (p.Entry.Reference is { } reference) _byReference[reference] = p.Entry.Id;
                break;
            case EntryReversed r:
                _reversedBy[r.Original] = r.Reversal;
                break;
            case PeriodClosed c:
                ClosedThrough = c.Through;
                break;
        }
    }

    // ---- queries ------------------------------------------------------------

    public bool IsReversed(Guid entryId) => _reversedBy.ContainsKey(entryId);

    /// <summary>Balance in the account's normal direction: an asset's debit balance is positive.</summary>
    public decimal Balance(string account, DateOnly? asOf = null)
    {
        var acc = _accounts.TryGetValue(account, out var a) ? a : throw new LedgerException($"Unknown account {account}");
        var lines = _entries.Values.Where(e => asOf is null || e.Date <= asOf)
            .SelectMany(e => e.Lines).Where(l => string.Equals(l.Account, account, StringComparison.OrdinalIgnoreCase));
        var net = lines.Sum(l => l.Debit - l.Credit);
        return acc.IsDebitNormal ? net : -net;
    }

    public TrialBalance TrialBalance(DateOnly asOf)
    {
        var rows = _accounts.Values.OrderBy(a => a.Code).Select(a =>
        {
            var lines = _entries.Values.Where(e => e.Date <= asOf).SelectMany(e => e.Lines)
                .Where(l => string.Equals(l.Account, a.Code, StringComparison.OrdinalIgnoreCase)).ToList();
            var net = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
            return new TrialBalanceRow(a, net > 0 ? net : 0, net < 0 ? -net : 0);
        }).Where(r => r.Debit != 0 || r.Credit != 0).ToList();
        return new TrialBalance(asOf, rows);
    }

    public IncomeStatement IncomeStatement(DateOnly from, DateOnly to)
    {
        decimal Sum(AccountType type) => _accounts.Values.Where(a => a.Type == type).Sum(a =>
        {
            var net = _entries.Values.Where(e => e.Date >= from && e.Date <= to).SelectMany(e => e.Lines)
                .Where(l => string.Equals(l.Account, a.Code, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.Debit - l.Credit);
            return a.IsDebitNormal ? net : -net;
        });
        return new IncomeStatement(from, to, Sum(AccountType.Income), Sum(AccountType.Expense));
    }
}

public sealed record TrialBalanceRow(Account Account, decimal Debit, decimal Credit);

public sealed record TrialBalance(DateOnly AsOf, IReadOnlyList<TrialBalanceRow> Rows)
{
    public decimal TotalDebits => Rows.Sum(r => r.Debit);
    public decimal TotalCredits => Rows.Sum(r => r.Credit);
    public bool Balances => TotalDebits == TotalCredits;
}

public sealed record IncomeStatement(DateOnly From, DateOnly To, decimal Income, decimal Expenses)
{
    public decimal NetProfit => Income - Expenses;
}
