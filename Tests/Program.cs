using LibraryDesk;

var Day = new DateOnly(2026, 11, 14);
var tests = new (string Label, Action Run)[]
{
    ("P01 Valid request", () => Equal(2, Ok(CheckoutRequest.Create(1, 2, Day)).BookId)),
    ("P02 Invalid IDs and date", () =>
    {
        Fail(CheckoutRequest.Create(0, 2, Day), "InvalidInput");
        Fail(CheckoutRequest.Create(1, 0, Day), "InvalidInput");
        Fail(CheckoutRequest.Create(1, 2, new DateOnly(2100, 1, 1)), "InvalidInput");
    }),
    ("P03 Missing lookup", () =>
    {
        var state = Sample();
        Fail(state.FindBook(999), "BookMissing");
        Fail(state.FindMember(999), "MemberMissing");
        Fail(state.FindLoan(999), "LoanMissing");
    }),
    ("P04 Same input, different policies", () =>
    {
        var state = Sample();
        var standard = Ok(Rules.Checkout(state, 1, 1, Day, Rules.StandardDays)).State.Loans[^1];
        var shortTerm = Ok(Rules.Checkout(state, 1, 1, Day, Rules.ShortDays)).State.Loans[^1];
        Equal(Day.AddDays(14), standard.Due);
        Equal(Day.AddDays(7), shortTerm.Due);
        Equal(2, state.Loans.Length);
        Equal(Day.AddDays(21), Ok(Rules.Checkout(state, 3, 1, Day, Rules.StandardDays)).State.Loans[^1].Due);
        Equal(Day.AddDays(10), Ok(Rules.Checkout(state, 3, 1, Day, Rules.ShortDays)).State.Loans[^1].Due);
    }),
    ("P05 Failure skips later stages", () =>
    {
        int calls = 0;
        int Policy(Member _) { calls++; return 14; }
        var state = Sample();
        Fail(Rules.Checkout(state, 99, 1, Day, Policy), "MemberMissing");
        Fail(Rules.Checkout(state, 1, 2, Day, Policy), "BookOnLoan");
        Equal(0, calls);
        Equal(2, state.Loans.Length);
    }),
    ("P06 Invalid policy and date boundary", () =>
    {
        var state = Sample();
        Fail(Rules.Checkout(state, 1, 1, Day, _ => 0), "InvalidTerm");
        Fail(Rules.Checkout(state, 1, 1, new DateOnly(2099, 12, 31), Rules.StandardDays), "InvalidTerm");
    }),
    ("P07 Payment requires a completed return", () =>
    {
        var state = Sample();
        Fail(Rules.PayFine(state, 1, 1000), "LoanOpen");
        Equal(false, state.Loans[0].Paid);
    }),
    ("P08 Renewal boundaries", () =>
    {
        var state = Sample();
        var loan = state.Loans[0];
        Fail(Rules.Renew(state, 1, loan.Due.AddDays(1)), "RenewalDate");
        var renewed = Ok(Rules.Renew(state, 1, loan.Due)).State;
        Equal(loan.Due.AddDays(7), renewed.Loans[0].Due);
        Equal(false, state.Loans[0].Renewed);
        Fail(Rules.Renew(renewed, 1, renewed.Loans[0].Due), "AlreadyRenewed");
        Fail(Rules.Return(renewed, 1, loan.Due.AddDays(-1), StubFine), "DateOrder");
    }),
    ("P09 Maximum active loans", () =>
    {
        var state = Sample();
        state = Ok(Rules.Checkout(state, 1, 1, Day, Rules.StandardDays)).State;
        state = Ok(Rules.Checkout(state, 1, 4, Day, Rules.StandardDays)).State;
        Fail(Rules.Checkout(state, 1, 5, Day, Rules.StandardDays), "LoanLimit");
    }),
    ("P10 Reservation prerequisites", () =>
    {
        var state = Sample();
        Fail(Rules.Reserve(state, 1, 1, Day), "BookAvailable");
        Fail(Rules.Reserve(state, 2, 2, Day), "OwnLoan");
        var reserved = Ok(Rules.Reserve(state, 3, 2, Day)).State;
        Fail(Rules.Reserve(reserved, 1, 2, Day), "AlreadyReserved");
        Fail(Rules.Renew(reserved, 1, reserved.Loans[0].Due), "Reserved");
        Fail(Rules.Return(reserved, 1, Day.AddDays(-1), StubFine), "DateOrder");
        Equal(0, state.Reservations.Length);
    }),
    ("P11 Curried filters and empty results", () =>
    {
        var state = Sample();
        Equal("1,2", string.Join(",", Rules.Overdue(state, Day, Rules.IsOverdue(Day)).Select(row => row.LoanId)));
        Equal(1, Rules.Overdue(state, Day, Rules.IsUrgent(Day)).Single().LoanId);
        Equal(0, Rules.Overdue(state, new DateOnly(2026, 11, 2), Rules.IsOverdue(new DateOnly(2026, 11, 2))).Length);
        Equal(0, Rules.Overdue(state with { Loans = [] }, Day, Rules.IsOverdue(Day)).Length);
        Equal(true, Rules.IsUrgent(state.Loans[0].Due.AddDays(7))(state.Loans[0]));
        Equal(false, Rules.IsUrgent(state.Loans[0].Due.AddDays(6))(state.Loans[0]));
    }),
    ("P12 Overdue ordering uses loan ID on ties", () =>
    {
        var state = Sample();
        state = state with { Loans = [state.Loans[1] with { Due = state.Loans[0].Due }, state.Loans[0]] };
        Equal("1,2", string.Join(",", Rules.Overdue(state, Day, Rules.IsOverdue(Day)).Select(row => row.LoanId)));
    }),
    ("P13 Activity groups, sums, ties and inclusive dates", () =>
    {
        var state = Ok(Rules.Return(Sample(), 1, Day, StubFine)).State;
        var rows = Rules.CategoryActivity(state, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 26));
        Equal(2, rows.Length);
        Equal(new CategoryRow("Fiction", 1, 1000), rows[0]);
        Equal(new CategoryRow("Programming", 1, 0), rows[1]);
        Equal(0, Rules.CategoryActivity(state, Day, Day).Length);
        Equal(0, Rules.CategoryActivity(state with { Loans = [] }, Day, Day).Length);
    }),
    ("P14 Iterator defers execution, snapshot is reusable", () =>
    {
        var state = Sample();
        int checks = 0;
        var pending = Rules.SelectLoans(state.Loans, loan => { checks++; return loan.Id == 2; });
        Equal(0, checks);
        var snapshot = pending.ToArray();
        Equal(2, checks);
        Equal(2, snapshot.Single().Id);
        Equal(2, snapshot.Single().Id);
        Equal(2, checks);
    }),
    ("P15 Dangling reference and overlapping loans rejected", () =>
    {
        var state = Sample();
        Fail(Storage.Validate(state with { Books = state.Books.RemoveAt(1) }), "InvalidData");
        Fail(Storage.Validate(state with { Loans = state.Loans.Add(state.Loans[0] with { Id = 54 }) }), "InvalidData");
        Ok(Storage.Validate(new Library([], [], [], [])));
    }),
    ("W01 Checkout then renew then on-time return", () =>
    {
        var original = Sample();
        var issued = Ok(Rules.Checkout(original, 1, 1, Day, Rules.StandardDays)).State;
        var renewed = Ok(Rules.Renew(issued, 3, Day.AddDays(14))).State;
        var returned = Ok(Rules.Return(renewed, 3, Day.AddDays(21), StubFine)).State;
        Equal(Day.AddDays(21), returned.Loans[^1].Returned!.Value);
        Equal(0, returned.Loans[^1].Fine);
        Equal(2, original.Loans.Length);
        Fail(Rules.Return(returned, 3, Day.AddDays(22), (_, _, _) =>
            throw new Exception("Should skip calculator.")), "AlreadyReturned");
        Ok(Storage.Validate(returned));
    }),
    ("W02 Reservation, native return, payment and pickup", () =>
    {
        var reserved = Ok(Rules.Reserve(Sample(), 3, 2, Day)).State;
        var returned = Ok(Rules.Return(reserved, 1, Day, NativeFine.Calculate)).State;
        Equal(1000, returned.Loans[0].Fine);
        Fail(Rules.Checkout(returned, 1, 2, Day, Rules.StandardDays), "Reserved");
        Fail(Rules.Checkout(returned, 2, 4, Day, Rules.StandardDays), "UnpaidFine");
        Fail(Rules.PayFine(returned, 1, 900), "WrongAmount");
        var paid = Ok(Rules.PayFine(returned, 1, 1000)).State;
        Equal(true, paid.Loans[0].Paid);
        Fail(Rules.PayFine(paid, 1, 1000), "AlreadyPaid");
        var pickedUp = Ok(Rules.Checkout(paid, 3, 2, Day, Rules.StandardDays)).State;
        Equal(0, pickedUp.Reservations.Length);
        Equal(3, pickedUp.Loans[^1].MemberId);
        Ok(Storage.Validate(pickedUp));
    }),
    ("F01 Missing file", () => InTemp(dir => Fail(Storage.Load(Path.Combine(dir, "missing.json")), "FileMissing"))),
    ("F02 Malformed JSON releases file", () => InTemp(dir =>
    {
        string path = Path.Combine(dir, "bad.json");
        File.WriteAllText(path, "{\"books\":[");
        Fail(Storage.Load(path), "MalformedJson");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    })),
    ("F03 Save/load round trip releases file", () => InTemp(dir =>
    {
        string path = Path.Combine(dir, "copy.json");
        var state = Ok(Rules.Checkout(Sample(), 1, 1, Day, Rules.StandardDays)).State;
        Ok(Storage.Save(path, Sample()));
        Ok(Storage.Save(path, state));
        Equal(state.Loans[^1], Ok(Storage.Load(path)).Loans[^1]);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Equal(1, Directory.GetFiles(dir).Length);
    })),
    ("F04 Failed replacement cleans staging file", () => InTemp(dir =>
    {
        string path = Path.Combine(dir, "target.json");
        Directory.CreateDirectory(path);
        string marker = Path.Combine(path, "original.txt");
        File.WriteAllText(marker, "keep");
        Fail(Storage.Save(path, Sample()), "WriteError");
        Equal("keep", File.ReadAllText(marker));
        Equal(0, Directory.GetFiles(dir).Length);
    })),
    ("N01 Native results for zero, days and cap", () =>
    {
        Equal(0, Ok(NativeFine.Calculate(0, 100, 1000)));
        Equal(300, Ok(NativeFine.Calculate(3, 100, 1000)));
        Equal(1000, Ok(NativeFine.Calculate(12, 100, 1000)));
        Equal(1000, Ok(NativeFine.Calculate(int.MaxValue, int.MaxValue, 1000)));
    }),
    ("N02 Native rejection and failed return safety", () =>
    {
        Fail(NativeFine.Calculate(-1, 100, 1000), "NativeRejected");
        var state = Sample();
        Fail(Rules.Return(state, 1, Day, (_, rate, cap) => NativeFine.Calculate(-1, rate, cap)), "NativeRejected");
        Equal<DateOnly?>(null, state.Loans[0].Returned);
        Equal(0, state.Loans[0].Fine);
    })
};

int passed = 0;
foreach (var (label, run) in tests)
{
    try
    {
        run();
        passed++;
        Console.WriteLine($"PASS {label}");
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {label}: {error.Message}");
    }
}

Console.WriteLine($"{passed}/{tests.Length} passed");
return passed == tests.Length ? 0 : 1;

static Library Sample() => Ok(Storage.Load(Path.Combine(AppContext.BaseDirectory, "library.json")));

static Result<int> StubFine(int days, int rate, int cap) => new Result<int>.Ok(Math.Min(days * rate, cap));

static T Ok<T>(Result<T> result) => result switch
{
    Result<T>.Ok success => success.Value,
    Result<T>.Fail failure => throw new Exception($"Expected Ok. Got {failure.Code}: {failure.Message}"),
    _ => throw new Exception("Unknown result.")
};

static void Fail<T>(Result<T> result, string code) =>
    Equal(code, result is Result<T>.Fail error ? error.Code : "Ok");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}

static void InTemp(Action<string> test)
{
    string dir = Path.Combine(Path.GetTempPath(), $"library-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try { test(dir); }
    finally { Directory.Delete(dir, recursive: true); }
}
