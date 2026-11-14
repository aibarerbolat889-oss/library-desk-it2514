# Library Desk

Introduction to Functional Programming · Final Exam · IT-2514  
Yerbolat Aibar, Bektore Salybek, Nurislam Ospanali

A small console app for a campus library desk. It issues, returns, renews, reserves books, tracks fine payments, and produces two reports. The menu takes explicit dates so the sample and tests work on any day.

## Run on Windows x64

Use the .NET 8 SDK for Windows x64 (verified with 8.0.131). The project uses the .NET base libraries and needs no NuGet dependencies. From the unzipped project folder:

```powershell
dotnet --version
dotnet build App/LibraryDesk.csproj
dotnet run --no-build --project App/LibraryDesk.csproj
dotnet run --project Tests/LibraryDesk.Tests.csproj
```

The default run creates `data/session.json` from `data/library.json` and saves each successful change there. To start an independent demo, use a fresh copy:

```powershell
Copy-Item data/library.json data/demo.json -Force
dotnet run --no-build --project App/LibraryDesk.csproj -- data/demo.json
```

When supplying a custom data path, create the parent directory and provide an existing valid JSON file. Use one active app instance per file. The test runner reports each assertion scenario as PASS or FAIL and returns exit code 1 if any scenario fails.

## Native fine library

The ZIP includes `runtime/win-x64/libraryfine.dll`. The app project copies it beside the managed output, and the test project receives it through its project reference. The app invokes the C export on every successful return, including returns with zero late days.

To rebuild the DLL, open **x64 Native Tools Command Prompt for VS 2022** with MSVC v143 and a Windows SDK installed, change to the project root and run:

```cmd
native\build.cmd
dotnet build App\LibraryDesk.csproj
dotnet run --project Tests\LibraryDesk.Tests.csproj
```

`native/fine.c` exports `calculate_fine` with three 32-bit integers and a 32-bit result. It uses a 64-bit intermediate and returns a negative status for invalid inputs. `NativeFine.Calculate` translates invalid arguments and load errors into a failed Result. On failure the return remains uncommitted.

## Rules and reports

| Menu | Workflow or report | Rule |
| --- | --- | --- |
| 1 | Checkout | An available, eligible book can be issued to a member with fewer than 3 active loans and no unpaid fine. A reservation gives exclusive pickup. |
| 2 | Return | Return an open loan on or after checkout and any recorded reservation or renewal. Fine = 100 KZT per late day, capped at 1000 KZT. |
| 3 | Renew | On its due date, extend an open unreserved loan once by 7 days. |
| 4 | Reserve | Another member may reserve a loaned book. One reservation per book. |
| 5 | Pay fine | Once the loan is closed, record the full unpaid amount exactly once. |
| 6 | Overdue | Active overdue loans, sorted by days late descending and loan ID ascending. Choose all overdue or 7+ days only. |
| 7 | Activity | Checkouts started in an inclusive date range, grouped by category with a count and assessed fine total. Ties sort by category. |
| 8 | Catalog | Browse book IDs, members, active loan IDs, reservations and unpaid fines. |

On the same fresh sample, checkout member 1 / book 1 / 2026-11-14 gives a due date of 2026-11-28 with Standard and 2026-11-21 with Short. Run each policy against its own fresh data copy. Staff terms are 21 and 10 days respectively.

## Defense practice

Use the fresh demo copy above. Each row lists the menu option followed by its inputs in order.

| Menu and inputs | Expected result |
| --- | --- |
| `6` → `2026-11-14` → `n` | Show loan 1 at 12 days and loan 2 at 5 days. With `y`, only loan 1. |
| `1` → `1` → `2` → `2026-11-14` → `1` | `BookOnLoan` |
| `5` → `1` → `1000` | `LoanOpen` |
| `4` → `3` → `2` → `2026-11-14` | Book 2 reserved for member 3. |
| `2` → `1` → `2026-11-14` | 12 late days, 1000 KZT fine via C. |
| `5` → `1` → `1000` | 1000 KZT paid for loan 1. |
| `1` → `3` → `2` → `2026-11-14` → `1` | Loan 3 issued, due 2026-12-05. |
| `1` → `1` → `1` → `2026-11-14` → `2` | Loan 4 issued, due 2026-11-21. |
| `3` → `4` → `2026-11-21` | Renewal until 2026-11-28. |
| `7` → `2026-10-01` → `2026-11-30` | Fiction: 2 checkouts, 1000 KZT. Programming: 2 checkouts, 0 KZT. |

For the live edit, use a practice copy, make one change and update its assertion. For example, change the student short term from 7 to 8 days and update P04, or change the urgent threshold from 7 to 8 and update the menu prompt and P11. Another option is to add an assertion in N01 that 10 days at 100 KZT reaches the cap exactly.

**Қорғауда:** 1) жұмыс істейтін сценарий мен бір қатені көрсетіңіз, 2) тесттерді іске қосыңыз, 3) өзіңіз өзгерткен әдіс пен тестті түсіндіріңіз, 4) сұралған шағын өзгерісті енгізіп, тестті қайта жүргізіңіз. Checkout тізбегі: тексеру → табу → мақұлдау → жаңа күй. `Bind` қате келсе келесі кезеңге өтпейді. `Map` сәтті мәнді жаңа мәнге түрлендіреді.

## Source map

- `App/Domain.cs` contains the immutable records, validated request, explicit results, checkout stages, rules and both reports.
- `App/Program.cs` is the console boundary. It commits and prints results after a successful save.
- `App/Storage.cs` loads and validates JSON, saves through a same-directory temporary file, and adapts the C function.
- `Tests/Program.cs` contains the deterministic assertions. P, W, F and N identify pure/domain, workflow, file and native cases.

The app uses one copy per book, preloaded books and members, and one local data file. Loan history and payment status stay in that file to support reporting and audit. Renewals are allowed on the due date only. Reservation cancellation/expiry, cash handling, multi-user coordination, and a graphical interface are outside this version.

## Submitted version

- Repository URL: <https://github.com/aibarerbolat889-oss/library-desk-it2514> (private).
- Branch: `main`.
- Snapshot commit: `$Format:%H$`. In a regular Git checkout, run `git rev-parse HEAD` to show this value. Git expands the hash in a ZIP produced by `git archive`.

The initial reference implementation and report materials were prepared with AI assistance. The course requires explicit instructor permission for AI use and each student to contribute real code and tests. Record verified contributions and the shared hosted revision before submission.
