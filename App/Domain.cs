using System.Collections.Immutable;

namespace LibraryDesk;

public abstract record Result<T>
{
    private Result() { }

    public sealed record Ok(T Value) : Result<T>;
    public sealed record Fail(string Code, string Message) : Result<T>;
}

public static class ResultExtensions
{
    public static Result<U> Map<T, U>(this Result<T> source, Func<T, U> transform) =>
        source.Bind(value => new Result<U>.Ok(transform(value)));

    public static Result<U> Bind<T, U>(this Result<T> source, Func<T, Result<U>> next) =>
        source switch
        {
            Result<T>.Ok success => next(success.Value),
            Result<T>.Fail failure => new Result<U>.Fail(failure.Code, failure.Message),
            _ => throw new InvalidOperationException("Unknown result.")
        };

    public static Result<T> Require<T>(this T? value, string code, string message) where T : class =>
        value is null ? new Result<T>.Fail(code, message) : new Result<T>.Ok(value);
}

public enum MemberKind { Student, Staff }
public sealed record Book(int Id, string Title, string Category);
public sealed record Member(int Id, string Name, MemberKind Kind);
public sealed record Loan(int Id, int BookId, int MemberId, DateOnly Start, DateOnly Due,
    bool Renewed = false, DateOnly? Returned = null, int Fine = 0, bool Paid = false);
public sealed record Reservation(int BookId, int MemberId, DateOnly Placed);

public sealed record Library(
    ImmutableArray<Book> Books, ImmutableArray<Member> Members,
    ImmutableArray<Loan> Loans, ImmutableArray<Reservation> Reservations)
{
    public Result<Book> FindBook(int id) =>
        Books.FirstOrDefault(book => book.Id == id).Require("BookMissing", "Book ID does not exist.");

    public Result<Member> FindMember(int id) =>
        Members.FirstOrDefault(member => member.Id == id).Require("MemberMissing", "Member ID does not exist.");

    public Result<Loan> FindLoan(int id) =>
        Loans.FirstOrDefault(loan => loan.Id == id).Require("LoanMissing", "Loan ID does not exist.");
}

public sealed class CheckoutRequest
{
    public int MemberId { get; }
    public int BookId { get; }
    public DateOnly Date { get; }

    private CheckoutRequest(int memberId, int bookId, DateOnly date) =>
        (MemberId, BookId, Date) = (memberId, bookId, date);

    public static Result<CheckoutRequest> Create(int memberId, int bookId, DateOnly date) =>
        memberId < 1 || bookId < 1 || !Rules.IsDate(date)
            ? new Result<CheckoutRequest>.Fail("InvalidInput", "Use positive IDs and a date from 2000 to 2099.")
            : new Result<CheckoutRequest>.Ok(new CheckoutRequest(memberId, bookId, date));
}

public sealed record Change(Library State, string Summary);
public sealed record CategoryRow(string Category, int Checkouts, long Fines);
public sealed record OverdueRow(int LoanId, string Title, string Member, DateOnly Due, int LateDays);

public static class Rules
{
    public const int DailyRate = 100;
    public const int FineCap = 1000;
    public const int RenewalDays = 7;
    public const int MaxActiveLoans = 3;

    public static bool IsDate(DateOnly date) => date.Year is >= 2000 and <= 2099;

    public static int StandardDays(Member member) => member.Kind switch
    {
        MemberKind.Student => 14,
        MemberKind.Staff => 21,
        _ => throw new ArgumentOutOfRangeException(nameof(member))
    };

    public static int ShortDays(Member member) => member.Kind switch
    {
        MemberKind.Student => 7,
        MemberKind.Staff => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(member))
    };

    public static Func<DateOnly, Func<Loan, bool>> LateBy(int minimumDays) =>
        date => loan => loan.Returned is null && date.DayNumber - loan.Due.DayNumber >= minimumDays;

    public static readonly Func<DateOnly, Func<Loan, bool>> IsOverdue = LateBy(1);
    public static readonly Func<DateOnly, Func<Loan, bool>> IsUrgent = LateBy(7);

    private sealed record ResolvedCheckout(CheckoutRequest Request, Book Book, Member Member);
    private sealed record ApprovedCheckout(Book Book, Member Member, DateOnly Start, DateOnly Due);
    private sealed record ReturnQuote(Loan Loan, DateOnly Date, int LateDays);

    public static Result<Change> Checkout(Library state, int memberId, int bookId, DateOnly date,
        Func<Member, int> loanDays) =>
        CheckoutRequest.Create(memberId, bookId, date)
            .Bind(request => ResolveCheckout(state, request))
            .Bind(resolved => ApproveCheckout(state, resolved, loanDays))
            .Map(approved => IssueLoan(state, approved));

    private static Result<ResolvedCheckout> ResolveCheckout(Library state, CheckoutRequest request) =>
        state.FindMember(request.MemberId).Bind(member =>
            state.FindBook(request.BookId).Map(book => new ResolvedCheckout(request, book, member)));

    private static Result<ApprovedCheckout> ApproveCheckout(Library state, ResolvedCheckout inputs,
        Func<Member, int> loanDays)
    {
        var (request, book, member) = inputs;
        if (state.Loans.Any(loan => loan.BookId == book.Id && loan.Returned is null))
            return Error<ApprovedCheckout>("BookOnLoan", "The book is already on loan.");
        if (state.Loans.Any(loan => loan.BookId == book.Id && loan.Returned > request.Date))
            return Error<ApprovedCheckout>("DateOrder", "Checkout precedes the previous return.");
        var reservation = state.Reservations.FirstOrDefault(item => item.BookId == book.Id);
        if (reservation is not null && reservation.MemberId != member.Id)
            return Error<ApprovedCheckout>("Reserved", "The book is reserved for another member.");
        if (reservation is not null && request.Date < reservation.Placed)
            return Error<ApprovedCheckout>("DateOrder", "Checkout precedes the reservation.");
        if (state.Loans.Count(loan => loan.MemberId == member.Id && loan.Returned is null) >= MaxActiveLoans)
            return Error<ApprovedCheckout>("LoanLimit", "The member has reached the active loan limit.");
        if (state.Loans.Any(loan => loan.MemberId == member.Id && loan.Fine > 0 && !loan.Paid))
            return Error<ApprovedCheckout>("UnpaidFine", "Pay the outstanding fine before checkout.");
        if (state.Loans.Any(loan => loan.Id == int.MaxValue))
            return Error<ApprovedCheckout>("IdLimit", "No loan IDs remain.");

        int days = loanDays(member);
        if (days is < 1 or > 365 || !IsDate(request.Date.AddDays(days)))
            return Error<ApprovedCheckout>("InvalidTerm", "The selected policy gives an invalid due date.");
        return new Result<ApprovedCheckout>.Ok(new(book, member, request.Date, request.Date.AddDays(days)));
    }

    private static Change IssueLoan(Library state, ApprovedCheckout approved)
    {
        int id = state.Loans.Select(loan => loan.Id).DefaultIfEmpty(0).Max() + 1;
        var loan = new Loan(id, approved.Book.Id, approved.Member.Id, approved.Start, approved.Due);
        var next = state with
        {
            Loans = state.Loans.Add(loan),
            Reservations = state.Reservations.RemoveAll(item => item.BookId == approved.Book.Id)
        };
        return new(next, $"Loan {id} issued. Due {loan.Due:yyyy-MM-dd}.");
    }

    public static Result<Change> Return(Library state, int loanId, DateOnly date,
        Func<int, int, int, Result<int>> calculateFine) =>
        state.FindLoan(loanId)
            .Bind(loan => QuoteReturn(state, loan, date))
            .Bind(quote => calculateFine(quote.LateDays, DailyRate, FineCap)
                .Bind(fine => FinishReturn(state, quote, fine)));

    private static Result<ReturnQuote> QuoteReturn(Library state, Loan loan, DateOnly date) => loan switch
    {
        { Returned: not null } => Error<ReturnQuote>("AlreadyReturned", "This loan is closed."),
        { Renewed: true } when date < loan.Due.AddDays(-RenewalDays) =>
            Error<ReturnQuote>("DateOrder", "Return cannot precede the recorded renewal day."),
        _ when !IsDate(date) || date < loan.Start =>
            Error<ReturnQuote>("InvalidDate", "Return date must be within 2000 to 2099 and on or after checkout."),
        _ when state.Reservations.Any(item => item.BookId == loan.BookId && date < item.Placed) =>
            Error<ReturnQuote>("DateOrder", "Return cannot precede the recorded reservation."),
        _ => new Result<ReturnQuote>.Ok(new(loan, date, Math.Max(0, date.DayNumber - loan.Due.DayNumber)))
    };

    private static Result<Change> FinishReturn(Library state, ReturnQuote quote, int fine)
    {
        if (fine < 0 || fine > FineCap)
            return Error<Change>("InvalidFine", "The calculator returned a fine outside the permitted range.");
        var returned = quote.Loan with { Returned = quote.Date, Fine = fine };
        return new Result<Change>.Ok(UpdateLoan(state, quote.Loan, returned,
            $"Loan {returned.Id} returned. Late days: {quote.LateDays}. Fine: {fine} KZT."));
    }

    public static Result<Change> Renew(Library state, int loanId, DateOnly date) =>
        state.FindLoan(loanId).Bind<Loan, Change>(loan => loan switch
        {
            { Returned: not null } => Error<Change>("AlreadyReturned", "A closed loan cannot be renewed."),
            { Renewed: true } => Error<Change>("AlreadyRenewed", "Only one renewal is allowed."),
            _ when !IsDate(date) || date != loan.Due || !IsDate(loan.Due.AddDays(RenewalDays)) =>
                Error<Change>("RenewalDate", "Renewal is allowed on the due date only, within the supported date range."),
            _ when state.Reservations.Any(item => item.BookId == loan.BookId) =>
                Error<Change>("Reserved", "A reserved book cannot be renewed."),
            _ => new Result<Change>.Ok(UpdateLoan(state, loan,
                loan with { Renewed = true, Due = loan.Due.AddDays(RenewalDays) },
                $"Loan {loan.Id} renewed. New due date: {loan.Due.AddDays(RenewalDays):yyyy-MM-dd}."))
        });

    public static Result<Change> Reserve(Library state, int memberId, int bookId, DateOnly date) =>
        state.FindMember(memberId).Bind(member =>
            state.FindBook(bookId).Bind<Book, Change>(book =>
            {
                if (!IsDate(date))
                    return Error<Change>("InvalidDate", "Use a date from 2000 to 2099.");
                if (state.Reservations.Any(item => item.BookId == book.Id))
                    return Error<Change>("AlreadyReserved", "The book already has a reservation.");
                var loan = state.Loans.FirstOrDefault(item => item.BookId == book.Id && item.Returned is null);
                if (loan is null)
                    return Error<Change>("BookAvailable", "The book is available. Use checkout.");
                if (loan.MemberId == member.Id)
                    return Error<Change>("OwnLoan", "You cannot reserve a book you are borrowing.");
                if (date < loan.Start)
                    return Error<Change>("DateOrder", "Reservation date precedes the active checkout.");
                var next = state with { Reservations = state.Reservations.Add(new(book.Id, member.Id, date)) };
                return new Result<Change>.Ok(new(next, $"Book {book.Id} reserved for member {member.Id}."));
            }));

    public static Result<Change> PayFine(Library state, int loanId, int amount) =>
        state.FindLoan(loanId).Bind<Loan, Change>(loan => loan switch
        {
            { Returned: null } => Error<Change>("LoanOpen", "Return the book before paying the fine."),
            { Fine: 0 } => Error<Change>("NoFine", "This loan has no fine."),
            { Paid: true } => Error<Change>("AlreadyPaid", "This fine has already been paid."),
            _ when amount != loan.Fine => Error<Change>("WrongAmount", $"The full fine is {loan.Fine} KZT."),
            _ => new Result<Change>.Ok(UpdateLoan(state, loan, loan with { Paid = true },
                $"Paid {amount} KZT for loan {loan.Id}."))
        });

    private static Change UpdateLoan(Library state, Loan oldLoan, Loan newLoan, string summary) =>
        new(state with { Loans = state.Loans.Replace(oldLoan, newLoan) }, summary);

    private static Result<T>.Fail Error<T>(string code, string message) => new(code, message);

    public static IEnumerable<Loan> SelectLoans(IEnumerable<Loan> loans, Func<Loan, bool> include)
    {
        foreach (var loan in loans)
            if (include(loan))
                yield return loan;
    }

    public static OverdueRow[] Overdue(Library state, DateOnly date, Func<Loan, bool> include)
    {
        var books = state.Books.ToDictionary(book => book.Id);
        var members = state.Members.ToDictionary(member => member.Id);
        return SelectLoans(state.Loans, include)
            .Select(loan => new OverdueRow(loan.Id, books[loan.BookId].Title, members[loan.MemberId].Name,
                loan.Due, Math.Max(0, date.DayNumber - loan.Due.DayNumber)))
            .OrderByDescending(row => row.LateDays).ThenBy(row => row.LoanId).ToArray();
    }

    public static CategoryRow[] CategoryActivity(Library state, DateOnly from, DateOnly to)
    {
        var books = state.Books.ToDictionary(book => book.Id);
        return state.Loans.Where(loan => loan.Start >= from && loan.Start <= to)
            .GroupBy(loan => books[loan.BookId].Category, StringComparer.Ordinal)
            .Select(group => new CategoryRow(group.Key, group.Count(), group.Sum(loan => (long)loan.Fine)))
            .OrderByDescending(row => row.Checkouts).ThenBy(row => row.Category, StringComparer.Ordinal).ToArray();
    }
}
