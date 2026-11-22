using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ApiTestRunner;

/// <summary>
/// Runs the suite. Built for long, unattended runs:
///  - every step is written to disk as soon as it finishes (ResultStream), then its heavy data is dropped
///  - assertion / capture / override lookups are indexed
///  - scenarios can run in parallel (Settings → MaxParallel); each scenario keeps its own captured values
///  - Ctrl+C stops cleanly and still produces results for everything that ran
///  - progress + ETA is printed periodically
/// </summary>
public sealed class TestRunner : IDisposable
{
    private readonly TestSuite _suite;
    private readonly HttpClientProvider _clients;
    private readonly StepExecutor _executor;
    private readonly VariableContext _rootVars;
    private readonly RunResult _run;
    private readonly ResultStream _stream;
    private readonly bool _keepResponses;
    private readonly int _detailLimit;
    private readonly bool _stopOnFailure;
    private readonly bool _consoleFailuresOnly;
    private readonly int _maxParallel;

    // ---- Re-login (JWT expiry on long runs) ----
    private readonly bool _refreshOn401;
    private readonly TimeSpan _refreshEvery;
    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private List<ScenarioDef> _setupDefs = new();
    private readonly List<ScenarioResult> _reloginResults = new();
    private int _setupGeneration;
    private bool _lastSetupOk = true;
    private TimeSpan _lastSetupAt;

    private bool _keepAllDetail;
    private int _failedDetailKept;
    private long _total, _done, _passed, _failed, _skipped;
    private readonly Stopwatch _clock = new();

    /// <summary>Raw responses per step - only kept for discover mode.</summary>
    public ConcurrentDictionary<string, StepResponse> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TestRunner(TestSuite suite, string environment, string runId, string outputFolder, bool keepResponses = false)
    {
        _suite = suite;
        _suite.BuildIndexes();
        _clients = new HttpClientProvider(suite);
        _executor = new StepExecutor(suite, _clients, environment);
        _rootVars = new VariableContext(suite, environment, runId);
        _stream = new ResultStream(outputFolder, suite);
        _keepResponses = keepResponses;
        _detailLimit = Math.Max(100, suite.SettingInt("ExcelDetailLimit", 5000));
        _stopOnFailure = suite.SettingBool("StopScenarioOnFailure", true);
        _consoleFailuresOnly = suite.Setting("ConsoleOutput", "All").Equals("Failures", StringComparison.OrdinalIgnoreCase);
        _maxParallel = Math.Clamp(suite.SettingInt("MaxParallel", 1), 1, 64);
        _refreshOn401 = suite.SettingBool("ReloginOn401", true);
        _refreshEvery = TimeSpan.FromMinutes(Math.Max(0, suite.SettingInt("ReloginEveryMinutes", 0)));
        _run = new RunResult
        {
            RunId = runId, Environment = environment, WorkbookPath = suite.WorkbookPath, StartedAt = DateTime.Now,
            MaxParallel = _maxParallel, ResultsFolder = outputFolder,
        };
    }

    private sealed record ScenarioDef(string Name, List<StepDef> Steps, bool IsSetup, int FirstRow);

    public async Task<RunResult> RunAsync(IReadOnlyCollection<string> scenarioFilter, CancellationToken ct)
    {
        var stopIfSetupFails = _suite.SettingBool("StopIfSetupFails", true);

        // Scenarios in the order they first appear in the sheet; _SETUP scenarios first.
        var selected = _suite.Steps
            .GroupBy(s => s.Scenario, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScenarioDef(g.Key, g.OrderBy(s => s.Step).ToList(), g.First().IsSetup, g.Min(s => s.SheetRow)))
            .Where(s => s.IsSetup || scenarioFilter.Count == 0 || scenarioFilter.Any(f => Matches(s.Name, f)))
            .Where(s => s.Steps.Any(st => st.Run))
            .OrderByDescending(x => x.IsSetup).ThenBy(x => x.FirstRow)
            .ToList();

        if (selected.All(s => s.IsSetup))
            ConsoleLog.Warn("No scenarios matched (or all have Run = N).");

        _total = selected.Sum(s => s.Steps.Count);
        _keepAllDetail = _total <= _detailLimit;
        ConsoleLog.ParallelMode = _maxParallel > 1;
        if (_maxParallel > 1) ConsoleLog.Info($"Running up to {_maxParallel} scenarios in parallel.");

        var setupDefs = selected.Where(s => s.IsSetup).ToList();
        _setupDefs = setupDefs;
        var mainDefs = selected.Where(s => !s.IsSetup).ToList();
        var setupResults = new List<ScenarioResult>();
        var mainResults = new ScenarioResult?[mainDefs.Count];

        _clock.Start();
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = ProgressLoopAsync(progressCts.Token);
        try
        {
            // 1) _SETUP scenarios, one after another. Their captures are shared with everyone.
            bool setupFailed = false;
            foreach (var def in setupDefs)
            {
                ct.ThrowIfCancellationRequested();
                var sr = await RunScenarioAsync(def, skipReason: null, ct);
                setupResults.Add(sr);
                if (sr.Outcome != Outcome.Pass) setupFailed = true;
            }
            _lastSetupOk = !setupFailed;
            _lastSetupAt = _clock.Elapsed;
            var skipAll = setupFailed && stopIfSetupFails ? "Skipped because a _SETUP scenario failed." : null;

            // 2) Everything else - sequential or in parallel.
            if (_maxParallel <= 1)
            {
                for (int i = 0; i < mainDefs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    mainResults[i] = await RunScenarioAsync(mainDefs[i], skipAll, ct);
                }
            }
            else
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallel, CancellationToken = ct };
                await Parallel.ForEachAsync(Enumerable.Range(0, mainDefs.Count), options, async (i, token) =>
                {
                    mainResults[i] = await RunScenarioAsync(mainDefs[i], skipAll, token);
                });
            }
        }
        catch (OperationCanceledException)
        {
            _run.Cancelled = true;
        }
        finally
        {
            progressCts.Cancel();
            try { await progress; } catch (OperationCanceledException) { }
            _clock.Stop();
            _stream.Dispose();
        }

        _run.Scenarios.AddRange(setupResults);
        lock (_reloginResults) _run.Scenarios.AddRange(_reloginResults);
        _run.Scenarios.AddRange(mainResults.Where(r => r is not null)!);
        _run.FinishedAt = DateTime.Now;
        if (_run.Cancelled)
            ConsoleLog.Warn($"\nRun cancelled - {_done:N0} of {_total:N0} steps ran. Writing results for what ran...");
        return _run;
    }

    private async Task<ScenarioResult> RunScenarioAsync(ScenarioDef def, string? skipReason, CancellationToken ct, string? displayName = null)
    {
        var sr = new ScenarioResult { Scenario = displayName ?? def.Name, Description = def.Steps.Select(s => s.ScenarioDescription).FirstOrDefault(d => d.Length > 0) ?? "" };
        if (!_consoleFailuresOnly && !ConsoleLog.ParallelMode) ConsoleLog.Scenario(sr.Scenario, sr.Description);

        // Setup scenarios write into the shared root context; others get their own.
        var vars = def.IsSetup ? _rootVars : _rootVars.ForScenario();
        bool skipRest = skipReason is not null;

        foreach (var step in def.Steps)
        {
            StepResult result;
            if (!step.Run) result = Skip(step, "Run = N");
            else if (skipRest) result = Skip(step, skipReason!);
            else if (ct.IsCancellationRequested) result = Cancelled(step);
            else
            {
                try { result = await RunStepAsync(step, def.IsSetup, vars, ct); }
                catch (OperationCanceledException) { result = Cancelled(step); }
            }

            Complete(result);
            sr.Steps.Add(result);

            if (result.Outcome is Outcome.Fail or Outcome.Error && _stopOnFailure && !skipRest)
            {
                skipRest = true;
                skipReason = $"Skipped because step {step.Step} did not pass.";
            }
        }
        return sr;
    }

    /// <summary>Stream the step to disk, update counters, print, then drop its heavy data if not needed in Excel.</summary>
    private void Complete(StepResult r)
    {
        _stream.Write(r);

        Interlocked.Increment(ref _done);
        switch (r.Outcome)
        {
            case Outcome.Pass: Interlocked.Increment(ref _passed); break;
            case Outcome.Skipped: Interlocked.Increment(ref _skipped); break;
            default: Interlocked.Increment(ref _failed); break;
        }

        if (!_consoleFailuresOnly || r.Outcome is Outcome.Fail or Outcome.Error) ConsoleLog.Step(r);

        bool keep = _keepAllDetail ||
                    (r.Outcome is Outcome.Fail or Outcome.Error && Interlocked.Increment(ref _failedDetailKept) <= _detailLimit);
        if (!keep) r.Compact();
    }

    private async Task ProgressLoopAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromSeconds(Math.Max(5, _suite.SettingInt("ProgressEverySeconds", 30)));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(every, ct);
            long done = Interlocked.Read(ref _done);
            if (done == 0 || _total == 0) continue;
            var elapsed = _clock.Elapsed;
            var eta = TimeSpan.FromTicks((long)(elapsed.Ticks / (double)done * (_total - done)));
            ConsoleLog.Progress($"Progress: {done:N0}/{_total:N0} steps ({100.0 * done / _total:0.0}%)  |  " +
                                $"pass {Interlocked.Read(ref _passed):N0}  fail {Interlocked.Read(ref _failed):N0}  skip {Interlocked.Read(ref _skipped):N0}  |  " +
                                $"elapsed {Fmt(elapsed)}  |  ETA {Fmt(eta)}  |  memory {GC.GetTotalMemory(false) / 1024 / 1024:N0} MB");
        }
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private async Task<StepResult> RunStepAsync(StepDef step, bool isSetup, VariableContext vars, CancellationToken ct)
    {
        // Timed re-login (Settings → ReloginEveryMinutes), e.g. a bit before the JWT expires.
        if (!isSetup && _refreshEvery > TimeSpan.Zero && _setupDefs.Count > 0 && _clock.Elapsed - _lastSetupAt >= _refreshEvery)
            await RefreshSetupAsync(Volatile.Read(ref _setupGeneration), "scheduled", ct);

        var result = new StepResult { Def = step, StartedAt = DateTime.Now, Method = step.Method };
        bool reloggedIn = false;
        StepExecutor.PreparedRequest req;
        try
        {
            req = _executor.Prepare(step, vars);
        }
        catch (StepConfigException ex)
        {
            result.Outcome = Outcome.Error;
            result.Error = "Setup problem: " + ex.Message;
            return result;
        }

        result.Url = req.Url;
        result.RequestHeaders = string.Join("\n", req.Headers.Select(h => $"{h.Key}: {h.Value}"));
        result.RequestBody = req.Body ?? "";
        result.Notes.AddRange(req.Notes);

        var assertionDefs = _suite.AssertionsFor(step).ToList();
        var maxAttempts = 1 + Math.Max(0, step.RetryCount);
        StepResponse? response = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            result.Attempts = attempt;
            result.Error = "";
            var generationAtSend = Volatile.Read(ref _setupGeneration);
            try
            {
                response = await _executor.SendAsync(req, vars, ct);
            }
            catch (StepConfigException ex)
            {
                result.Outcome = Outcome.Error;
                result.Error = "Setup problem: " + ex.Message;
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is HttpRequestException or TaskCanceledException or System.Data.Common.DbException or InvalidOperationException or IOException)
            {
                response = null;
                result.Error = ex is TaskCanceledException ? $"Timed out after {_clients.Timeout.TotalSeconds:0}s calling {req.Url}" : $"Could not call {req.Url}: {ex.GetBaseException().Message}";
                if (ex is HttpRequestException && req.Url.StartsWith("https", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(ex.ToString(), "SSL|TLS|handshake|certificate|ended prematurely|forcibly closed|connection reset", RegexOptions.IgnoreCase))
                    result.Error += req.ClientCertificate.Length > 0
                        ? " (SSL handshake failed - check the Client Certificate for this service is the one the server trusts, and that the server's certificate is trusted on this PC.)"
                        : " (SSL handshake failed - if this service uses two-way SSL, fill Client Certificate for it in the Environments sheet.)";
                result.Outcome = Outcome.Error;
                if (attempt < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(step.RetryWaitSeconds), ct); continue; }
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything unexpected (driver/config problems): mark this step as ERROR instead of crashing the run.
                result.Error = $"Unexpected error in step ({ex.GetType().Name}): {ex.GetBaseException().Message}";
                result.Outcome = Outcome.Error;
                return result;
            }

            // 401 on a normal step: the token probably expired. Log in again once and repeat this call.
            if (response.HttpStatus == 401 && !isSetup && !reloggedIn && _refreshOn401 && _setupDefs.Count > 0)
            {
                reloggedIn = true;
                if (await RefreshSetupAsync(generationAtSend, "HTTP 401", ct))
                {
                    try { req = _executor.Prepare(step, vars); }
                    catch (StepConfigException ex) { result.Outcome = Outcome.Error; result.Error = "Setup problem: " + ex.Message; return result; }
                    result.Url = req.Url;
                    result.RequestHeaders = string.Join("\n", req.Headers.Select(h => $"{h.Key}: {h.Value}"));
                    result.RequestBody = req.Body ?? "";
                    result.Notes.Add("Got HTTP 401 - logged in again and repeated the call.");
                    attempt--;           // the repeat doesn't use up a retry
                    continue;
                }
                result.Notes.Add("Got HTTP 401 - tried to log in again but the _SETUP scenario failed.");
            }

            result.HttpStatus = response.HttpStatus;
            result.DurationMs = response.DurationMs;
            result.ResponseBody = response.BodyText;
            result.ResponseHeaders = string.Join("\n", response.Headers.Select(h => $"{h.Key}: {h.Value}"));

            if (assertionDefs.Count == 0)
            {
                // No checks defined: at least make sure the call itself succeeded.
                var ok = response.HttpStatus is null || (response.HttpStatus >= 200 && response.HttpStatus < 300);
                result.Assertions = new()
                {
                    new AssertionResult
                    {
                        Def = new AssertionDef { Scenario = step.Scenario, Step = step.Step, FieldPath = "HTTP Status", Check = "2xx (default)", Description = "No assertions defined for this step" },
                        Expected = "200-299", Actual = response.HttpStatus?.ToString() ?? "(n/a)", Passed = ok, Message = ok ? "" : "No assertions defined; default check expects HTTP 2xx",
                    },
                };
            }
            else
            {
                result.Assertions = assertionDefs.Select(a => AssertionEngine.Evaluate(a, response, vars)).ToList();
            }

            if (result.Assertions.All(a => a.Passed)) break;
            if (attempt < maxAttempts)
            {
                if (!_consoleFailuresOnly) ConsoleLog.Retry(step, attempt, maxAttempts, step.RetryWaitSeconds);
                await Task.Delay(TimeSpan.FromSeconds(step.RetryWaitSeconds), ct);
            }
        }

        if (_keepResponses) Responses[step.Key] = response!;
        result.Outcome = result.Assertions.All(a => a.Passed) ? Outcome.Pass : Outcome.Fail;

        // Captures (only meaningful when we have a response).
        foreach (var cap in _suite.CapturesFor(step))
        {
            var (found, node, text) = response!.Read(vars.Resolve(cap.FieldPath, EscapeMode.None));
            if (!found || node is null)
            {
                result.Outcome = Outcome.Fail;
                result.Error = AppendErr(result.Error, $"Capture '{cap.SaveAs}': field '{cap.FieldPath}' not found in response.");
                continue;
            }
            vars.SetScenario(cap.SaveAs, text);
            if (isSetup) vars.SetGlobal(cap.SaveAs, text);
            result.Captured[cap.SaveAs] = text;
        }
        return result;
    }

    /// <summary>
    /// Runs the _SETUP scenarios again (e.g. login) so new tokens replace the expired ones.
    /// Only one refresh happens per expiry, even if many parallel scenarios get 401 at the same time:
    /// callers pass the generation they used, and if someone already refreshed since then, they just reuse it.
    /// </summary>
    private async Task<bool> RefreshSetupAsync(int seenGeneration, string reason, CancellationToken ct)
    {
        await _setupLock.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _setupGeneration) != seenGeneration) return _lastSetupOk; // already refreshed by someone else

            var n = _setupGeneration + 1;
            ConsoleLog.Warn($"   ↻ Logging in again ({reason}) - re-running {string.Join(", ", _setupDefs.Select(d => d.Name))} (#{n})");
            bool ok = true;
            foreach (var def in _setupDefs)
            {
                Interlocked.Add(ref _total, def.Steps.Count);
                var sr = await RunScenarioAsync(def, null, ct, $"{def.Name} (re-login #{n})");
                lock (_reloginResults) _reloginResults.Add(sr);
                if (sr.Outcome != Outcome.Pass) ok = false;
            }
            _lastSetupOk = ok;
            _lastSetupAt = _clock.Elapsed;
            Interlocked.Increment(ref _setupGeneration);
            return ok;
        }
        finally
        {
            _setupLock.Release();
        }
    }

    private static StepResult Cancelled(StepDef step)
    {
        var r = Skip(step, "Not run - the run was cancelled (Ctrl+C).");
        r.CancelledStep = true;
        return r;
    }

    private static string AppendErr(string existing, string add) => string.IsNullOrEmpty(existing) ? add : existing + " " + add;

    private static StepResult Skip(StepDef step, string why) =>
        new() { Def = step, Outcome = Outcome.Skipped, Error = why, StartedAt = DateTime.Now, Method = step.Method };

    private static bool Matches(string name, string pattern)
    {
        var rx = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _clients.Dispose();
    }
}

public static class ConsoleLog
{
    private static readonly object Gate = new();

    /// <summary>When scenarios run in parallel, each step line is prefixed with its scenario.</summary>
    public static bool ParallelMode { get; set; }

    public static void Progress(string msg)
    {
        lock (Gate) Write(ConsoleColor.Cyan, $"   ⏱ {msg}\n");
    }

    public static void Scenario(string name, string desc)
    {
        lock (Gate) ScenarioUnlocked(name, desc);
    }

    private static void ScenarioUnlocked(string name, string desc)
    {
        Console.WriteLine();
        Write(ConsoleColor.Cyan, $"▶ {name}");
        if (desc.Length > 0) Write(ConsoleColor.DarkGray, $"  {desc}");
        Console.WriteLine();
    }

    public static void Step(StepResult r)
    {
        lock (Gate) StepUnlocked(r);
    }

    private static void StepUnlocked(StepResult r)
    {
        var (color, label) = r.Outcome switch
        {
            Outcome.Pass => (ConsoleColor.Green, "PASS"),
            Outcome.Fail => (ConsoleColor.Red, "FAIL"),
            Outcome.Error => (ConsoleColor.Magenta, "ERROR"),
            _ => (ConsoleColor.DarkGray, "SKIP"),
        };
        Write(color, $"   [{label,-5}]");
        var status = r.HttpStatus is int s ? $" HTTP {s}" : "";
        var time = r.Outcome == Outcome.Skipped ? "" : $" {r.DurationMs} ms";
        var prefix = ParallelMode ? $"{r.Def.Scenario} › " : "";
        Console.Write($" {prefix}Step {r.Def.Step} {r.Def.StepName} ({r.Method}{status}{time})");
        if (r.Captured.Count > 0) Write(ConsoleColor.DarkCyan, "  captured: " + string.Join(", ", r.Captured.Keys));
        Console.WriteLine();
        if (r.Outcome != Outcome.Pass && r.FailureReason.Length > 0)
            Write(ConsoleColor.DarkYellow, $"           → {r.FailureReason}\n");
    }

    public static void Retry(StepDef step, int attempt, int max, int wait)
    {
        var prefix = ParallelMode ? $"{step.Scenario} › " : "";
        lock (Gate) Write(ConsoleColor.DarkYellow, $"   ... {prefix}step {step.Step}: checks not met yet (attempt {attempt}/{max}), retrying in {wait}s\n");
    }

    public static void Warn(string msg) => Write(ConsoleColor.Yellow, msg + "\n");
    public static void Error(string msg) => Write(ConsoleColor.Red, msg + "\n");
    public static void Info(string msg) { lock (Gate) Console.WriteLine(msg); }

    public static void Write(ConsoleColor color, string text)
    {
        lock (Gate) WriteUnlocked(color, text);
    }

    private static void WriteUnlocked(ConsoleColor color, string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = old;
    }
}
