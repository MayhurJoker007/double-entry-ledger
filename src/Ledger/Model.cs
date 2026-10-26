namespace Ledger;

public enum AccountType { Asset, Liability, Equity, Income, Expense }

public sealed record Account(string Code, string Name, AccountType Type)
{
    /// <summary>Assets and expenses grow with debits; liabilities, equity and income grow with credits.</summary>
    public bool IsDebitNormal => Type is AccountType.Asset or AccountType.Expense;
}

public sealed record EntryLine(string Account, decimal Debit, decimal Credit, string? Memo = null)
{
    public static EntryLine Dr(string account, decimal amount, string? memo = null) => new(account, amount, 0m, memo);
    public static EntryLine Cr(string account, decimal amount, string? memo = null) => new(account, 0m, amount, memo);
}

public sealed record JournalEntry(
    Guid Id,
    DateOnly Date,
    string Description,
    string? Reference,
    IReadOnlyList<EntryLine> Lines,
    Guid? Reverses = null)
{
    public decimal Total => Lines.Sum(l => l.Debit);
}

public class LedgerException(string message) : Exception(message);

/// <summary>Someone else wrote to the ledger after we loaded it. Reload and try again.</summary>
public class ConcurrencyException(string stream, long expected, long actual)
    : Exception($"Stream {stream} is at version {actual}, expected {expected}");
