# API Regression Test Runner

This tool runs API regression tests from an Excel workbook. SAs write the test cases in Excel. The runner calls the real APIs, chains values from one call to the next (for example, placement → inquiry), checks the responses, and writes an Excel results file that can be kept as evidence.

The runner is .NET 8 and **needs no NuGet packages**: it reads and writes Excel itself. It also needs no PowerShell, Node or Postman.

```
ApiRegressionTester/
├─ ApiTestRunner/        the runner (.NET 8 console app)
├─ MockOrderApi/         fake order API for trying everything locally
├─ TestPack/             ← the folder you hand to SAs
│  ├─ TestCases.xlsx     test cases (the only file SAs edit, plus templates)
│  ├─ Templates/         request body templates (.json) and SQL checks (.sql)
│  ├─ run_tests.bat      run everything
│  ├─ discover.bat       list every field a scenario returns (for writing assertions)
│  ├─ validate.bat       check the workbook for mistakes without calling APIs
│  └─ bin/               compiled runner
└─ build_testpack.bat    rebuilds TestPack\bin as ApiTestRunner.exe
```

## Try it in 2 minutes (local mock)

```cmd
cd ApiRegressionTester
dotnet run --project MockOrderApi            :: terminal 1, listens on http://localhost:5080
TestPack\run_tests.bat                       :: terminal 2, uses DefaultEnvironment = LOCAL
```

You should see 7 scenarios pass, and the results workbook opens automatically.

Inquiry steps show "retrying" a couple of times. That's expected: the mock makes orders visible to inquiry only after 1.5 s, which shows the Retry Count feature working.

## Point it at SIT

1. **Environments sheet:** replace the `SIT1` Base URLs with the real hosts. There is one row per microservice (`Service` = Auth, OrderAPI, ...).
2. **Variables sheet:** fill in the SIT1 test data (customer, account, CASA, fund codes).
3. **Secrets:** keep them out of Excel. Write `{{$env:SIT1_CLIENT_SECRET}}` in the sheet and set the value once per machine with `setx SIT1_CLIENT_SECRET "..."` in cmd.
4. **Templates:** replace the sample `place_order_*.json` and `order_inquiry.json` with your real request contracts. Field names in PayloadOverrides, Assertions and Capture must match them.
5. **Login step:** `_SETUP-LOGIN` calls AuthenticateLogin with `LoginMethod = Override` (no OAuth). Match `Templates\authenticate_login.json` to your real `AuthenticateLoginRequest` fields, and set `loginUserId` in the Variables sheet.
6. Run `run_tests.bat SIT1`, or set `DefaultEnvironment = SIT1` in Settings.

## How SAs use it

The **How To Use** sheet inside the workbook is the SA guide. In short:

| Sheet | Purpose |
|---|---|
| **TestCases** | One row per API call. Rows with the same Scenario run in Step order. `Run = N` skips a row. |
| **PayloadOverrides** | Changes only what differs from the template. `<REMOVE>` drops a field (first-time investor without AccountNo), and `<NULL>`, `<EMPTY>`, `{{variables}}` and `header:Name` also work. |
| **Assertions** | One check per row: Field Path, Check (dropdown, 18 types), Expected. |
| **Capture** | Saves a response value (e.g. `OrderReferenceNo` → `orderRefNo`) for use as `{{orderRefNo}}` in later steps. |
| Variables / Headers / Environments / Settings | Usually set once by the lead or dev. |

`discover.bat` runs one scenario and produces a list of **every field path with its sample value**. SAs copy paths from there into Assertions, so they never have to read raw JSON.

## Command line (for pipelines)

```cmd
ApiTestRunner run      --file TestCases.xlsx --env SIT1 [--scenario BUY-*,SELL-EXISTING] [--out D:\results] [--open]
ApiTestRunner discover --file TestCases.xlsx --env SIT1 --scenario BUY-EXISTING
ApiTestRunner validate --file TestCases.xlsx
```

Exit codes: `0` all passed · `1` test failures · `2` workbook/config problem · `3` cancelled. Because of these, the runner can be added as a post-deployment step in Azure DevOps or Jenkins.

## Login with a JWT cookie

AuthenticateLogin returns the JWT in `Set-Cookie: access_token=...` (HttpOnly), and `ResponseDetails.Token` is nulled. The workbook handles this as follows:

- **Capture:** `_SETUP-LOGIN | 1 | accessToken | Cookie:access_token` reads the cookie value.
- **Headers sheet:** `Cookie | access_token={{accessToken}}` sends the cookie on every later call, the same way a browser would. If a service reads `Authorization: Bearer` instead, add a header row `Authorization | Bearer {{accessToken}}`.
- **Cookie handling is explicit:** the runner does **not** keep cookies automatically. Every cookie that is sent is visible in the Headers sheet. This also matters for microservices: browser-style cookies wouldn't be sent across different hosts anyway.
- **Assertions on login:** the sample assertions also check that the cookie is `httponly` and that `ResponseDetails.Token` is empty, i.e. the token isn't leaked in the body.
- **Masking:** cookie values and Authorization headers are always masked (`****abcd`) in results and payload files.

## Services without JWT, and two-way SSL (mutual TLS)

**Login is optional.** For a product stream without JWT:

- Set `Run = N` on `_SETUP-LOGIN` (or delete it).
- Delete the `Cookie` row in the Headers sheet. You can also leave it: a header whose `{{value}}` was never captured is simply not sent.

**Two-way SSL needs no change to the test cases.** The certificate is a property of the *service*, so you only fill two extra columns on that service's row in the Environments sheet:

| Environment | Service | Base URL | Client Certificate | Certificate Password |
|---|---|---|---|---|
| SIT1 | AccountAPI | https://sit1-account.yourbank.local | `certs\sit1-client.pfx` | `{{$env:SIT1_CERT_PASSWORD}}` |
| SIT1 | CardAPI | https://sit1-card.yourbank.local | `thumbprint:3A1B…` | *(blank - cert is in the Windows store)* |

Details:

- **Client Certificate** can be either:
  - a `.pfx`/`.p12` file, with a path relative to the workbook, or
  - `thumbprint:<value>` for a certificate already installed under Current User or Local Machine → Personal. This is typical on bank laptops, and it's the same certificate Postman or the browser uses.
- **Mixing services:** JWT services and two-way SSL services can be mixed in one run, or even in one scenario. Each service gets the right HTTP client.
- **Server trust:** the SIT server's certificate must be trusted on the PC, which it usually is via the bank's internal CA. `IgnoreSslErrors = Y` only skips *server* certificate checks. It does not replace the client certificate.
- **Readable errors** for the common problems: wrong .pfx password, a `.cer` with no private key, an expired certificate, a thumbprint not found, a missing environment variable, and a handshake rejected because no certificate was configured.

## Field paths: finding items in lists

| Field Path | Meaning |
|---|---|
| `ResponseDetails.Results[0].Status` | first item of the list |
| `ResponseDetails.Results[cdvNo={{cdvNo}}].Status` | the item whose `cdvNo` equals the captured value, wherever it is in the list |
| `ResponseDetails.Results[cdvNo={{cdvNo}}]` + `exists` | "my record is in the list" |
| `ResponseDetails.Results.Count` | number of items, usable with `greater than`, `equals` and the other number checks (`.Length` also works) |

These paths work in both Assertions and Capture. `{{variables}}` can be used inside a Field Path. Numbers compare by value, so `[amount=100]` matches `100.00`.

## Long runs, parallel scenarios and big suites

The runner is built to run unattended for hours. The relevant **Settings** are:

| Setting | Default | What it does |
|---|---|---|
| `MaxParallel` | 1 | How many **scenarios** run at the same time. Steps inside a scenario always run in order, and each scenario keeps its own captured values. Run time drops roughly in proportion: 2,000 calls at 100 ms each took 211 s with 1, 55 s with 4, 29 s with 8 and 16 s with 16. Raise it only as far as the test environment can handle. |
| `ReloginOn401` | Y | If a call gets **HTTP 401** (e.g. the JWT expired), the `_SETUP` scenarios run again **once**, even when many parallel scenarios hit 401 together, and the call is repeated with the new token. This has no effect in projects without `_SETUP`. |
| `ReloginEveryMinutes` | 0 | Optionally log in again on a schedule, e.g. a few minutes before the JWT lifetime. 0 = only on 401. |
| `SavePayloads` | All | `All` / `Failed` / `None`: which steps get request, response and header files. Use `Failed` for very large runs. |
| `ExcelDetailLimit` | 5000 | Above this many steps, the Excel file lists **failures only**, and everything else is in the CSV files. Memory stays flat either way. |
| `ConsoleOutput` | All | `Failures` prints only failed steps plus progress lines, which is better for long runs. |
| `ProgressEverySeconds` | 30 | Prints progress lines: steps done / total, pass/fail counts, elapsed time, ETA and memory. |
| `IsDebug` | N | `Y` keeps the console window open at the end so developers can read the log. `N` closes it automatically. Can also be passed on the command line as `--IsDebug true`. It never blocks when run from a pipeline. |

**How a long run is protected:**
- **Results are saved as the run goes.** Every step is written to `steps.csv`, `checks.csv` and `payloads\<scenario>\` the moment it finishes. A crash or power cut still leaves everything that ran.
- **Ctrl+C.** The first Ctrl+C stops starting new steps, waits for in-flight calls, and writes the Excel file marked **CANCELLED - PARTIAL RESULTS** (exit code 3). Interrupted scenarios are shown as skipped, not failed. A second Ctrl+C quits immediately.
- **Memory stays flat.** Once a step is saved, its bodies are dropped from memory unless they're needed for the Excel file. Assertion, capture and override lookups are indexed, so 100k steps don't slow down lookups.
- **Connections are refreshed.** HTTP connections are recycled every 5 minutes, so DNS or load-balancer changes during a multi-hour run are picked up.
- **Tested** with 30,000 calls, 8 scenarios in parallel and tokens expiring every 5 seconds. Result: all 30 deliberately broken checks reported, no false failures, one re-login per expiry, and memory between 110 and 260 MB.

**Unique reference numbers when running in parallel.** Values built from the clock (`{{$now:...}}`) **can repeat** when two scenarios run in the same millisecond. Use these instead:
- `{{$timestamp}}`: yyyyMMddHHmmssfff, never repeats within the runner.
- `{{$unique:12}}`: a guaranteed-unique number of exactly 12 digits (12 to 20 allowed). Use it for fields with a max length, e.g. `ManualRefNo`.

## Results

`TestPack\Results\Run_<env>_<time>\` contains:

- `Results_<env>_<time>.xlsx`, which has 4 sheets:
  - **Summary:** pass/fail per scenario, with the first failure reason
  - **Steps:** URL, HTTP status, time taken, attempts, captured values
  - **Checks:** every assertion with expected vs actual
  - **Payloads:** full request and response for each step
- `steps.csv` / `checks.csv`: one line per step / per check, written live during the run.
- `payloads\<scenario>\stepN_request.json`, `stepN_response.json` and `stepN_headers.txt`: the full request and response of each step, for attaching to SIT evidence.

Every tab keeps its header row frozen. Data rows use one fixed height, set by `Settings → ResultRowHeight` (default 60 points, about 4 lines; `0` = Excel auto-fit). Double-click a row border to expand a single row.

Fields listed in `Settings → MaskFields` are shown as `****1234`. Authorization, tokens, passwords and client secrets are always masked.

## Optional: DB verification steps

A step with `Method = SQL` runs a query against the connection string in the Environments sheet for that Service. `{{placeholders}}` become SQL **parameters**, not string concatenation. Assertions then read `RowCount` and `Rows[0].ColumnName`.

This needs `Microsoft.Data.SqlClient`, so it is off by default. To enable it:

```cmd
dotnet publish ApiTestRunner -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSql=true -o TestPack\bin
```

The sample `BUY-EXISTING` step 3 shows how it works (it's set to `Run = N`). Update `Templates\check_order_in_db.sql` to match your real table names.

## Behaviour worth knowing

- **_SETUP scenarios** (name starts with `_SETUP`) run first. Values they capture are visible to every scenario. If a setup scenario fails, the rest of the run is skipped (`StopIfSetupFails`).
- **Failures within a scenario:** after a failed step, the remaining steps of that scenario are skipped (`StopScenarioOnFailure`), because they usually depend on the failed step's captured values.
- **Retry Count / Retry Wait** repeat a step with the *same* request until all its checks pass. Use them for inquiry-after-placement where processing is asynchronous.
- **No assertions:** a step with no assertions passes if the HTTP status is 2xx. For APIs that return business errors with HTTP 200 (like AuthenticateLogin's general exception path), always also assert `ResponseHeader.ErrorCode` / `ResponseStatus`.
- **Data types:** override values keep the data type the template already has, so `Amount: 1000.00` stays a number when overridden with `5000`. Use `<NUMBER>` or `<JSON>` to force a type.
- **Comparing values:** `equals` treats `5000` and `5000.00` as equal, but keeps codes like `0000` vs `0` different.
- **Workbook layout:** the workbook is read by header names, so SAs can reorder columns or add their own note columns.
- **Dynamic fields:** fields that change on every call (RequestId, RequestAt, ResponseAt) should use `is not empty` or `matches pattern`, never `equals`.
