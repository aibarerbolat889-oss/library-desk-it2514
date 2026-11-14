using System.Globalization;
using LibraryDesk;

if (args.Length > 1)
{
    Console.Error.WriteLine("Usage: LibraryDesk [data-file.json]");
    return 2;
}

string path = args.Length == 1 ? args[0] : Path.Combine("data", "session.json");
while (true)
{
    var loaded = Storage.Load(path);
    if (loaded is Result<Library>.Fail { Code: "FileMissing" } && args.Length == 0)
        loaded = Storage.Load(Path.Combine("data", "library.json")).Bind(state => Storage.Save(path, state));

    if (loaded is Result<Library>.Fail failure)
    {
        Console.Error.WriteLine($"[{failure.Code}] {failure.Message}");
        return 1;
    }

    var state = ((Result<Library>.Ok)loaded).Value;
    Console.WriteLine("""

        LIBRARY DESK
        1 Checkout    2 Return      3 Renew       4 Reserve
        5 Pay fine    6 Overdue     7 Activity    8 Catalog
        0 Exit
        """);
    Console.Write("Choice: ");
    string? choice = Console.ReadLine()?.Trim();
    if (choice is null or "0")
        return 0;

    try
    {
        switch (choice)
        {
            case "1":
                Save(Rules.Checkout(state, ReadInt("Member ID"), ReadInt("Book ID"), ReadDate("Checkout date"),
                    ReadPolicy()));
                break;
            case "2":
                Save(Rules.Return(state, ReadInt("Loan ID"), ReadDate("Return date"), NativeFine.Calculate));
                break;
            case "3":
                Save(Rules.Renew(state, ReadInt("Loan ID"), ReadDate("Renewal date")));
                break;
            case "4":
                Save(Rules.Reserve(state, ReadInt("Member ID"), ReadInt("Book ID"), ReadDate("Reservation date")));
                break;
            case "5":
                Save(Rules.PayFine(state, ReadInt("Loan ID"), ReadInt("Amount in KZT")));
                break;
            case "6":
            {
                var date = ReadDate("Report date");
                var rows = Read("7+ days only? (y/n)").ToLowerInvariant() switch
                {
                    "y" => Rules.Overdue(state, date, Rules.IsUrgent(date)),
                    "n" => Rules.Overdue(state, date, Rules.IsOverdue(date)),
                    _ => throw new FormatException("Enter y or n.")
                };
                Console.WriteLine($"{rows.Length} overdue loan(s)");
                foreach (var row in rows)
                    Console.WriteLine($"#{row.LoanId} | {row.Title} | {row.Member} | due {row.Due:yyyy-MM-dd} | {row.LateDays} day(s)");
                break;
            }
            case "7":
            {
                var from = ReadDate("From");
                var to = ReadDate("To");
                if (from > to)
                    throw new FormatException("From date cannot be after To date.");
                var rows = Rules.CategoryActivity(state, from, to);
                Console.WriteLine($"{rows.Length} category group(s)");
                foreach (var row in rows)
                    Console.WriteLine($"{row.Category} | {row.Checkouts} checkout(s) | {row.Fines} KZT assessed");
                break;
            }
            case "8":
                foreach (var book in state.Books.OrderBy(book => book.Id))
                {
                    var loan = state.Loans.FirstOrDefault(loan => loan.BookId == book.Id && loan.Returned is null);
                    var reservation = state.Reservations.FirstOrDefault(item => item.BookId == book.Id);
                    string status = loan is null ? "Available" : $"Loan #{loan.Id}, due {loan.Due:yyyy-MM-dd}";
                    string waiting = reservation is null ? "" : $", reserved for member #{reservation.MemberId}";
                    Console.WriteLine($"Book #{book.Id} | {book.Title} | {status}{waiting}");
                }
                foreach (var member in state.Members.OrderBy(member => member.Id))
                    Console.WriteLine($"Member #{member.Id} | {member.Name} | {member.Kind}");
                foreach (var loan in state.Loans.Where(loan => loan.Fine > 0 && !loan.Paid))
                    Console.WriteLine($"Loan #{loan.Id} | unpaid fine {loan.Fine} KZT");
                break;
            default:
                Console.WriteLine("Choose a menu number from 0 to 8.");
                break;
        }
    }
    catch (FormatException error)
    {
        Console.WriteLine($"[InvalidInput] {error.Message}");
    }
    catch (EndOfStreamException)
    {
        return 0;
    }
}

void Save(Result<Change> result)
{
    var saved = result.Bind(change => Storage.Save(path, change.State).Map(_ => change.Summary));
    Console.WriteLine(saved switch
    {
        Result<string>.Ok success => success.Value,
        Result<string>.Fail error => $"[{error.Code}] {error.Message}",
        _ => throw new InvalidOperationException("Unknown result.")
    });
}

static string Read(string label)
{
    Console.Write($"{label}: ");
    return Console.ReadLine()?.Trim() ?? throw new EndOfStreamException();
}

static int ReadInt(string label) =>
    int.TryParse(Read(label), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
        ? value : throw new FormatException($"{label} must be a positive whole number.");

static DateOnly ReadDate(string label) =>
    DateOnly.TryParseExact(Read($"{label} (yyyy-MM-dd)"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) && Rules.IsDate(date)
        ? date : throw new FormatException("Enter a date in yyyy-MM-dd format from 2000 to 2099.");

static Func<Member, int> ReadPolicy() => Read("Policy (1 standard, 2 short)") switch
{
    "1" => Rules.StandardDays,
    "2" => Rules.ShortDays,
    _ => throw new FormatException("Choose policy 1 or 2.")
};
