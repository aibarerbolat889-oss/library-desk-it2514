using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LibraryDesk;

public static class Storage
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static Result<Library> Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Library>(stream, Format)
                .Require("InvalidData", "The JSON document cannot be null.")
                .Bind(Validate);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Result<Library>.Fail("FileMissing", $"Data file not found: {path}");
        }
        catch (JsonException error)
        {
            return new Result<Library>.Fail("MalformedJson", $"Invalid JSON at {error.Path ?? "$"}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return new Result<Library>.Fail("ReadError", error.Message);
        }
    }

    public static Result<Library> Save(string path, Library state)
    {
        string? temporary = null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".library-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, state, Format);
            File.Move(temporary, fullPath, overwrite: true);
            temporary = null;
            return new Result<Library>.Ok(state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return new Result<Library>.Fail("WriteError", error.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { /* Keep the original write error. */ }
                catch (UnauthorizedAccessException) { /* Keep the original write error. */ }
            }
        }
    }

    public static Result<Library> Validate(Library state)
    {
        if (state.Books.IsDefault || state.Members.IsDefault || state.Loans.IsDefault ||
            state.Reservations.IsDefault)
            return Invalid("Expected books, members, loans and reservations arrays.");

        if (state.Books.Any(book => book is null || book.Id < 1 || string.IsNullOrWhiteSpace(book.Title) ||
            string.IsNullOrWhiteSpace(book.Category)) ||
            state.Members.Any(member => member is null || member.Id < 1 || string.IsNullOrWhiteSpace(member.Name) ||
                !Enum.IsDefined(member.Kind)))
            return Invalid("Invalid book or member.");

        var books = state.Books.Select(book => book.Id).ToHashSet();
        var members = state.Members.Select(member => member.Id).ToHashSet();
        if (books.Count != state.Books.Length || members.Count != state.Members.Length)
            return Invalid("Duplicate book or member ID.");

        if (state.Loans.Any(loan => loan is null || loan.Id < 1 || !books.Contains(loan.BookId) ||
            !members.Contains(loan.MemberId) || !Rules.IsDate(loan.Start) || !Rules.IsDate(loan.Due) ||
            loan.Due <= loan.Start || (loan.Renewed && loan.Due.DayNumber - loan.Start.DayNumber <= Rules.RenewalDays) ||
            (loan.Returned is DateOnly returned && (!Rules.IsDate(returned) || returned < loan.Start ||
                (loan.Renewed && returned < loan.Due.AddDays(-Rules.RenewalDays)))) ||
            loan.Fine != ExpectedFine(loan) || (loan.Paid && loan.Fine == 0)))
            return Invalid("Invalid loan references, dates or fine state.");

        if (state.Loans.Select(loan => loan.Id).Distinct().Count() != state.Loans.Length ||
            state.Loans.GroupBy(loan => loan.BookId).Any(HasOverlap) ||
            state.Loans.Where(loan => loan.Returned is null).GroupBy(loan => loan.MemberId)
                .Any(group => group.Count() > Rules.MaxActiveLoans))
            return Invalid("Duplicate loan ID, overlapping book loans or member above the active loan limit.");

        if (state.Reservations.Any(item => item is null || !books.Contains(item.BookId) ||
            !members.Contains(item.MemberId) || !Rules.IsDate(item.Placed) ||
            !state.Loans.Any(loan => loan.BookId == item.BookId && loan.MemberId != item.MemberId &&
                loan.Start <= item.Placed && (loan.Returned is null || loan.Returned >= item.Placed))) ||
            state.Reservations.GroupBy(item => item.BookId).Any(group => group.Count() > 1))
            return Invalid("Invalid or duplicate reservation.");

        return new Result<Library>.Ok(state);
    }

    private static bool HasOverlap(IEnumerable<Loan> loans)
    {
        Loan? previous = null;
        foreach (var loan in loans.OrderBy(item => item.Start).ThenBy(item => item.Id))
        {
            if (previous is not null && (previous.Returned is null || previous.Returned > loan.Start))
                return true;
            previous = loan;
        }
        return false;
    }

    private static int ExpectedFine(Loan loan) => loan.Returned is DateOnly day
        ? Math.Min(Rules.FineCap, Math.Max(0, day.DayNumber - loan.Due.DayNumber) * Rules.DailyRate)
        : 0;

    private static Result<Library>.Fail Invalid(string message) => new("InvalidData", message);
}

public static class NativeFine
{
    [DllImport("libraryfine", EntryPoint = "calculate_fine", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int CalculateNative(int days, int rate, int cap);

    public static Result<int> Calculate(int days, int rate, int cap)
    {
        try
        {
            int fine = CalculateNative(days, rate, cap);
            return fine < 0
                ? new Result<int>.Fail("NativeRejected", "The C library rejected a negative argument.")
                : new Result<int>.Ok(fine);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new Result<int>.Fail("NativeUnavailable",
                "libraryfine.dll could not be loaded. Build or restore the Windows x64 DLL and rebuild the app.");
        }
    }
}
