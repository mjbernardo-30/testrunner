using System.Text;
using ApiTestRunner;

Console.OutputEncoding = Encoding.UTF8;

// ------------------------------------------------------------------
//  ApiTestRunner run      [--file TestCases.xlsx] [--env SIT1] [--scenario BUY-*,SELL-001] [--out Results]
//  ApiTestRunner discover [--file TestCases.xlsx] [--env SIT1]  --scenario BUY-EXISTING
//  ApiTestRunner validate [--file TestCases.xlsx]
// ------------------------------------------------------------------

var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "run";
var opts = ParseOptions(args);

// IsDebug: true  = keep the console window open at the end (developers checking logs)
//          false = close automatically (default). Command line wins over the Settings sheet.
//   ApiTestRunner run --IsDebug true      or   Settings sheet → IsDebug = Y
bool? debugFromCli = opts.TryGetValue("IsDebug", out var dbg) || opts.TryGetValue("debug", out dbg)
    ? (dbg ?? "true").Trim().ToLowerInvariant() is "true" or "y" or "yes" or "1"
    : null;
bool isDebug = debugFromCli ?? false;

if (command is "help" or "-h" or "--help" or "/?")
{
    PrintHelp();
    return Finish(0);
}
if (command is not ("run" or "discover" or "validate"))
{
    ConsoleLog.Error($"Unknown command '{command}'.");
    PrintHelp();
    return Finish(2);
}

var file = opts.GetValueOrDefault("file") ?? FindDefaultWorkbook();
if (file is null || !File.Exists(file))
{
    ConsoleLog.Error($"Test workbook not found: {file ?? "TestCases.xlsx"}. Use --file <path>.");
    return Finish(2);
}

TestSuite suite;
var problems = new List<string>();
try
{
    suite = WorkbookLoader.Load(file, problems);
}
catch (IOException ex)
{
    ConsoleLog.Error($"Could not read {file}: {ex.Message}");
    return Finish(2);
}
catch (InvalidDataException ex)
{
    ConsoleLog.Error($"{file} does not look like an .xlsx workbook: {ex.Message}");
    return Finish(2);
}

isDebug = debugFromCli ?? suite.SettingBool("IsDebug", false);

var env = opts.GetValueOrDefault("env") ?? suite.Setting("DefaultEnvironment");
if (string.IsNullOrWhiteSpace(env))
    problems.Add("No environment chosen. Set 'DefaultEnvironment' in the Settings sheet or pass --env SIT1.");
else if (!suite.Environments.Any(e => e.Environment.Equals(env, StringComparison.OrdinalIgnoreCase)))
    problems.Add($"Environment '{env}' is not in the Environments sheet. Available: {string.Join(", ", suite.Environments.Select(e => e.Environment).Distinct(StringComparer.OrdinalIgnoreCase))}.");

ConsoleLog.Info($"Workbook    : {Path.GetFullPath(file)}");
ConsoleLog.Info($"Environment : {env}");
ConsoleLog.Info($"Scenarios   : {suite.Steps.Select(s => s.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).Count()}  |  steps: {suite.Steps.Count}  |  checks: {suite.Assertions.Count}");

if (problems.Count > 0)
{
    ConsoleLog.Error($"\nThe workbook has {problems.Count} problem(s) to fix first:");
    foreach (var p in problems) ConsoleLog.Warn("  - " + p);
    return Finish(2);
}
if (command == "validate")
{
    ConsoleLog.Write(ConsoleColor.Green, "\nWorkbook looks good.\n");
    return Finish(0);
}

var filter = (opts.GetValueOrDefault("scenario") ?? "")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .ToList();
if (command == "discover" && filter.Count == 0)
{
    ConsoleLog.Error("discover needs --scenario <ID> (e.g. --scenario BUY-EXISTING).");
    return Finish(2);
}

var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
IReadOnlyDictionary<string, StepResponse> responses;
var resultsRoot = opts.GetValueOrDefault("out") ?? suite.Setting("ResultsFolder", "Results");
if (!Path.IsPathRooted(resultsRoot)) resultsRoot = Path.Combine(suite.WorkbookFolder, resultsRoot);
var outputFolder = Path.Combine(resultsRoot, $"Run_{env}_{runId}");
Directory.CreateDirectory(outputFolder);

using var cts = new CancellationTokenSource();
// 1st Ctrl+C: stop starting new steps and write results for what ran. 2nd Ctrl+C: exit immediately.
Console.CancelKeyPress += (_, e) =>
{
    if (cts.IsCancellationRequested) return; // let the second Ctrl+C terminate the process
    e.Cancel = true;
    ConsoleLog.Warn("\nCtrl+C - stopping after the current calls and saving results (press Ctrl+C again to quit immediately)...");
    cts.Cancel();
};

RunResult result;
using (var runner = new TestRunner(suite, env!, runId, outputFolder, keepResponses: command == "discover"))
{
    result = await runner.RunAsync(filter, cts.Token);
    responses = runner.Responses;
}

string report;
try
{
    report = command == "discover"
        ? ReportWriter.WriteDiscover(result, responses, suite, outputFolder)
        : ReportWriter.Write(result, suite, outputFolder);
}
catch (IOException ex)
{
    ConsoleLog.Error($"Could not write results (is the file open in Excel?): {ex.Message}");
    return Finish(2);
}

var setupFailures = result.Scenarios.Count(s => s.Scenario.StartsWith("_SETUP", StringComparison.OrdinalIgnoreCase) && s.Outcome is Outcome.Fail or Outcome.Error);
var scen = result.Scenarios.Where(s => !s.Scenario.StartsWith("_SETUP", StringComparison.OrdinalIgnoreCase)).ToList();
int pass = scen.Count(s => s.Outcome == Outcome.Pass);
int fail = scen.Count(s => s.Outcome is Outcome.Fail or Outcome.Error) + setupFailures;
int skip = scen.Count(s => s.Outcome == Outcome.Skipped);
if (setupFailures > 0) ConsoleLog.Error($"\n{setupFailures} _SETUP scenario(s) failed - check the token / login step first.");

Console.WriteLine();
Console.WriteLine(new string('─', 60));
ConsoleLog.Write(fail == 0 ? ConsoleColor.Green : ConsoleColor.Red,
    $"{(fail == 0 ? "ALL PASSED" : "FAILURES FOUND")}  —  {pass} passed, {fail} failed, {skip} skipped\n");
Console.WriteLine($"{(command == "discover" ? "Field list" : "Results")}: {report}");
if (command != "discover") Console.WriteLine($"Full detail (CSV + payloads): {outputFolder}");
if (result.Cancelled) ConsoleLog.Warn("Run was cancelled - results above are partial.");

if (opts.ContainsKey("open")) TryOpen(report);
return Finish(result.Cancelled ? 3 : command == "discover" ? 0 : fail == 0 ? 0 : 1);

// ------------------------------------------------------------------

// Every exit goes through here so IsDebug can keep the window open.
int Finish(int exitCode)
{
    if (isDebug && !Console.IsInputRedirected)
    {
        Console.WriteLine();
        ConsoleLog.Write(ConsoleColor.DarkGray, $"IsDebug = true (exit code {exitCode}) - press any key to close this window...");
        Console.ReadKey(intercept: true);
        Console.WriteLine();
    }
    return exitCode;
}

static Dictionary<string, string?> ParseOptions(string[] args)
{
    var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        d[key] = value;
    }
    return d;
}

static string? FindDefaultWorkbook()
{
    foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var p = Path.Combine(dir, "TestCases.xlsx");
        if (File.Exists(p)) return p;
    }
    return null;
}

static void TryOpen(string path)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
    catch { /* not fatal */ }
}

static void PrintHelp()
{
    Console.WriteLine("""
    API Regression Test Runner

      ApiTestRunner run      [--file TestCases.xlsx] [--env SIT1] [--scenario BUY-*,SELL-EXISTING] [--out Results] [--open]
          Runs the scenarios in the workbook and writes Results_<env>_<time>.xlsx.
          --scenario accepts a comma-separated list; * is a wildcard. _SETUP scenarios always run.

      ApiTestRunner discover --scenario BUY-EXISTING [--file TestCases.xlsx] [--env SIT1] [--open]
          Runs the scenario once and lists every field in each response, so you can copy
          Field Paths into the Assertions / Capture sheets.

      ApiTestRunner validate [--file TestCases.xlsx]
          Checks the workbook for mistakes without calling any API.

    Add --IsDebug true to keep this window open at the end (or Settings → IsDebug = Y).

    Exit codes: 0 = all passed, 1 = test failures, 2 = workbook/config problem, 3 = cancelled (partial results saved).
    """);
}
