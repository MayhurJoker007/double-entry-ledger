using System.Globalization;
using Ledger;
using static Ledger.EntryLine;

// A month of a small wholesaler's books, then the reports. Run it twice: the second run replays the same events
// from ledger.db, and the references stop the entries being posted again.

var store = new SqliteEventStore("Data Source=ledger.db");
var books = GeneralLedger.Load(store, "demo");
var za = CultureInfo.GetCultureInfo("en-ZA");

if (books.Accounts.Count == 0)
{
    books.OpenAccount("1000", "Bank", AccountType.Asset);
    books.OpenAccount("1100", "Debtors", AccountType.Asset);
    books.OpenAccount("1200", "Stock", AccountType.Asset);
    books.OpenAccount("2000", "Creditors", AccountType.Liability);
    books.OpenAccount("2100", "VAT", AccountType.Liability);
    books.OpenAccount("3000", "Owner's capital", AccountType.Equity);
    books.OpenAccount("4000", "Sales", AccountType.Income);
    books.OpenAccount("5000", "Cost of sales", AccountType.Expense);
    books.OpenAccount("6100", "Rent", AccountType.Expense);
    books.OpenAccount("6200", "Fuel", AccountType.Expense);
}

var d = (int day) => new DateOnly(2026, 3, day);
books.Post(d(1), "Capital introduced", [Dr("1000", 80_000m), Cr("3000", 80_000m)], "CAP-001");
books.Post(d(2), "Stock from Highveld Beverages", [Dr("1200", 34_500m), Cr("2000", 34_500m)], "BILL-7781");
books.Post(d(3), "Rent, March", [Dr("6100", 9_000m), Cr("1000", 9_000m)], "RENT-2026-03");
books.Post(d(9), "Invoice INV-1001, Soweto Sports Tavern", [Dr("1100", 28_750m), Cr("4000", 25_000m), Cr("2100", 3_750m)], "INV-1001");
books.Post(d(9), "Cost of INV-1001", [Dr("5000", 17_200m), Cr("1200", 17_200m)], "COS-1001");
books.Post(d(14), "Fuel", [Dr("6200", 2_340.55m), Cr("1000", 2_340.55m)], "FUEL-0314");
books.Post(d(20), "Payment received INV-1001", [Dr("1000", 28_750m), Cr("1100", 28_750m)], "RCPT-1001");
books.Post(d(25), "Paid Highveld Beverages", [Dr("2000", 34_500m), Cr("1000", 34_500m)], "PAY-7781");
books.Save(store);

var monthEnd = d(31);
var tb = books.TrialBalance(monthEnd);
Console.WriteLine($"Trial balance at {monthEnd:dd MMMM yyyy}");
foreach (var row in tb.Rows)
    Console.WriteLine($"  {row.Account.Code}  {row.Account.Name,-18}{Fmt(row.Debit),14}{Fmt(row.Credit),14}");
Console.WriteLine($"  {"",-24}{tb.TotalDebits.ToString("N2", za),14}{tb.TotalCredits.ToString("N2", za),14}  {(tb.Balances ? "balanced" : "DOES NOT BALANCE")}");

var pnl = books.IncomeStatement(d(1), monthEnd);
Console.WriteLine();
Console.WriteLine($"March: income {pnl.Income.ToString("N2", za)}, expenses {pnl.Expenses.ToString("N2", za)}, profit {pnl.NetProfit.ToString("N2", za)}");

string Fmt(decimal v) => v == 0 ? "" : v.ToString("N2", za);
