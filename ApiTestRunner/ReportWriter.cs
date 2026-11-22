using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiTestRunner.Xlsx;

namespace ApiTestRunner;

public sealed class Masker
{
    private readonly HashSet<string> _fields;

    public Masker(TestSuite suite)
    {
        var defaults = "Authorization,Cookie,Set-Cookie,Password,AccessToken,access_token,Token,ClientSecret,client_secret";
        _fields = (defaults + "," + suite.Setting("MaskFields"))
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool ShouldMask(string name) => _fields.Contains(name);

    public string Value(string v) => v.Length <= 4 ? "****" : "****" + v[^4..];

    public string Json(string text)
    {
        var node = JsonPathUtil.TryParse(text);
        if (node is null) return text;
        Walk(node);
        return node.ToJsonString(JsonPathUtil.Pretty);
    }

    private void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (ShouldMask(key) && child is JsonValue v) obj[key] = Value(JsonPathUtil.ToText(v));
                    else Walk(child);
                }
                break;
            case JsonArray arr:
                foreach (var c in arr) Walk(c);
                break;
        }
    }

    public string Headers(string headerLines) =>
        string.Join("\n", ExpandSetCookie(headerLines).Select(line =>
        {
            var idx = line.IndexOf(':');
            if (idx < 0) return line;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                return $"{name}: {Regex.Replace(value, @"(^|;\s*)([^=;\s]+)=([^;]*)", m => IsCookieAttr(m.Groups[2].Value) ? m.Value : $"{m.Groups[1].Value}{m.Groups[2].Value}={Value(m.Groups[3].Value)}")}";
            return ShouldMask(name) ? $"{name}: {Value(value)}" : line;
        }));

    // "Set-Cookie: a=1\nb=2" → one "Set-Cookie:" line per cookie
    private static IEnumerable<string> ExpandSetCookie(string headerLines)
    {
        string? last = null;
        foreach (var line in headerLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            var looksLikeHeader = idx > 0 && !line[..idx].Contains('=') && !line[..idx].Contains(' ');
            if (!looksLikeHeader && last is not null) { yield return $"{last}: {line.Trim()}"; continue; }
            last = idx > 0 ? line[..idx].Trim() : null;
            yield return line;
        }
    }

    private static bool IsCookieAttr(string n) =>
        n.ToLowerInvariant() is "path" or "domain" or "expires" or "max-age" or "samesite";

    public string Url(string url) => Regex.Replace(url, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=****");
}

public static class ReportWriter
{
    /// <summary>
    /// Builds the Excel results file. Small runs: every step, check and payload.
    /// Large runs (more steps than Settings → ExcelDetailLimit): Excel lists failures only, and the
    /// complete detail is in steps.csv / checks.csv / payloads\ (already written while the run was going).
    /// </summary>
    public static string Write(RunResult run, TestSuite suite, string outputFolder)
    {
        const int ExcelMaxRows = 1_000_000;
        var mask = new Masker(suite);
        var limit = Math.Max(100, suite.SettingInt("ExcelDetailLimit", 5000));
        var allSteps = run.Scenarios.SelectMany(s => s.Steps).ToList();
        bool full = allSteps.Count <= limit;
        static bool Bad(Outcome o) => o is Outcome.Fail or Outcome.Error;

        var xl = new XlsxWriter();
        // One row height for every result tab. Settings → ResultRowHeight (points, 0 = Excel auto-fit).
        var rowHeight = suite.SettingInt("ResultRowHeight", suite.SettingInt("PayloadRowHeight", 60));
        var csvNote = $"Complete detail for every step: steps.csv, checks.csv and the payloads folder in {Path.GetFileName(outputFolder)}.";

        // ---------------- Summary ----------------
        var sum = xl.AddSheet("Summary").ColumnWidths(28, 50, 12, 14, 14, 14, 80);
        int passed = run.Scenarios.Count(s => s.Outcome == Outcome.Pass);
        int failed = run.Scenarios.Count(s => Bad(s.Outcome));
        int skipped = run.Scenarios.Count(s => s.Outcome == Outcome.Skipped);
        var duration = run.FinishedAt - run.StartedAt;
        sum.AddRow(XCell.S("API Regression Test Results", CellStyle.Title));
        sum.AddRow();
        sum.AddRow(XCell.S("Run ID", CellStyle.Bold), run.RunId);
        sum.AddRow(XCell.S("Environment", CellStyle.Bold), run.Environment);
        sum.AddRow(XCell.S("Workbook", CellStyle.Bold), Path.GetFileName(run.WorkbookPath));
        sum.AddRow(XCell.S("Started", CellStyle.Bold), run.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        sum.AddRow(XCell.S("Finished", CellStyle.Bold), $"{run.FinishedAt:yyyy-MM-dd HH:mm:ss}   (took {(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}, parallel scenarios: {run.MaxParallel})");
        sum.AddRow(XCell.S("Overall Result", CellStyle.Bold),
            run.Cancelled ? XCell.S($"CANCELLED - PARTIAL RESULTS ({failed} scenario(s) failed so far)", CellStyle.Warn) :
            failed == 0 ? XCell.S("ALL PASSED", CellStyle.Pass) : XCell.S($"{failed} SCENARIO(S) FAILED", CellStyle.Fail));
        sum.AddRow(XCell.S("Scenarios", CellStyle.Bold), $"{run.Scenarios.Count:N0} total  |  {passed:N0} passed  |  {failed:N0} failed  |  {skipped:N0} skipped");
        sum.AddRow(XCell.S("Steps", CellStyle.Bold), $"{allSteps.Count:N0} total  |  {allSteps.Count(s => s.Outcome == Outcome.Pass):N0} passed  |  {allSteps.Count(s => Bad(s.Outcome)):N0} failed  |  {allSteps.Count(s => s.Outcome == Outcome.Skipped):N0} skipped");
        sum.AddRow(XCell.S("Detail", CellStyle.Bold), full ? csvNote : $"Large run: Excel tabs list FAILED items only (up to {limit:N0}). {csvNote}");
        sum.AddRow();
        int headerRow = sum.RowCount + 1;
        sum.AddRow(H("Scenario"), H("Description"), H("Result"), H("Steps Passed"), H("Steps Total"), H("Duration (ms)"), H("First Failure"));
        var scenRows = full ? run.Scenarios : run.Scenarios.Where(s => Bad(s.Outcome)).Take(limit).ToList();
        foreach (var s in scenRows)
        {
            var firstFail = s.Steps.FirstOrDefault(x => Bad(x.Outcome));
            sum.AddRow(s.Scenario, XCell.S(s.Description, CellStyle.Wrap), Result(s.Outcome),
                s.Steps.Count(x => x.Outcome == Outcome.Pass), s.Steps.Count, s.DurationMs,
                XCell.S(firstFail is null ? "" : $"Step {firstFail.Def.Step} ({firstFail.Def.StepName}): {firstFail.FailureReason}", CellStyle.Wrap));
        }
        sum.Filter(headerRow, 7);
        sum.Freeze(headerRow);                                   // title, run info and table header stay visible
        sum.FixedRowHeight(rowHeight, fromRow: headerRow + 1);   // only the scenario rows

        // ---------------- Steps ----------------
        var steps = xl.AddSheet("Steps").ColumnWidths(24, 6, 26, 10, 8, 55, 11, 13, 9, 13, 40, 60, 20).Freeze(1)
            .FixedRowHeight(rowHeight);
        steps.AddRow(H("Scenario"), H("Step"), H("Step Name"), H("Result"), H("Method"), H("URL"), H("HTTP Status"), H("Duration (ms)"), H("Attempts"), H("Checks Passed"), H("Captured Values"), H("Failure Reason / Notes"), H("Started At"));
        var stepRows = (full ? allSteps : allSteps.Where(s => Bad(s.Outcome)).Take(limit)).Take(ExcelMaxRows);
        foreach (var s in stepRows)
        {
            var captured = string.Join("\n", s.Captured.Select(c => $"{c.Key} = {(mask.ShouldMask(c.Key) ? mask.Value(c.Value) : c.Value)}"));
            var notes = string.Join("\n", new[] { s.FailureReason }.Concat(s.Notes).Where(n => !string.IsNullOrWhiteSpace(n)));
            steps.AddRow(s.Def.Scenario, s.Def.Step, s.Def.StepName, Result(s.Outcome), s.Method, XCell.S(mask.Url(s.Url), CellStyle.Wrap),
                s.HttpStatus is int hs ? XCell.N(hs) : "", s.Outcome == Outcome.Skipped ? "" : XCell.N(s.DurationMs),
                s.Attempts == 0 ? "" : XCell.N(s.Attempts),
                s.ChecksTotal == 0 ? "" : $"{s.ChecksPassed}/{s.ChecksTotal}",
                XCell.S(captured, CellStyle.Wrap), XCell.S(notes, CellStyle.Wrap), s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        }
        steps.Filter(1, 13);

        // ---------------- Checks ----------------
        // Only steps whose detail is still in memory (all steps for small runs, failed ones for large runs).
        var checks = xl.AddSheet("Checks").ColumnWidths(24, 6, 55, 18, 30, 30, 10, 45, 30).Freeze(1)
            .FixedRowHeight(rowHeight);
        checks.AddRow(H("Scenario"), H("Step"), H("Field Path"), H("Check"), H("Expected"), H("Actual"), H("Result"), H("Message"), H("Description"));
        foreach (var s in allSteps.Where(x => x.HasDetail && (full || Bad(x.Outcome))))
        {
            foreach (var a in s.Assertions)
            {
                if (checks.RowCount >= ExcelMaxRows) break;
                checks.AddRow(s.Def.Scenario, s.Def.Step, a.Def.FieldPath, a.Def.Check, XCell.S(a.Expected, CellStyle.Wrap), XCell.S(a.Actual, CellStyle.Wrap),
                    a.Passed ? XCell.S("PASS", CellStyle.Pass) : XCell.S("FAIL", CellStyle.Fail), XCell.S(a.Message, CellStyle.Wrap), XCell.S(a.Def.Description, CellStyle.Wrap));
            }
        }
        checks.Filter(1, 9);

        // ---------------- Payloads ----------------
        var pay = xl.AddSheet("Payloads").ColumnWidths(24, 6, 26, 40, 70, 40, 70).Freeze(1)
            .FixedRowHeight(rowHeight);
        pay.AddRow(H("Scenario"), H("Step"), H("Step Name"), H("Request Headers"), H("Request Body"), H("Response Headers"), H("Response Body"));
        foreach (var s in allSteps.Where(x => x.HasDetail && x.Outcome != Outcome.Skipped && (full || Bad(x.Outcome))).Take(limit))
        {
            pay.AddRow(s.Def.Scenario, s.Def.Step, s.Def.StepName,
                XCell.S(mask.Headers(s.RequestHeaders), CellStyle.Wrap), XCell.S(mask.Json(JsonPathUtil.PrettyOrRaw(s.RequestBody)), CellStyle.Wrap),
                XCell.S(mask.Headers(s.ResponseHeaders), CellStyle.Wrap), XCell.S(mask.Json(JsonPathUtil.PrettyOrRaw(s.ResponseBody)), CellStyle.Wrap));
        }
        pay.Filter(1, 7);

        var file = Path.Combine(outputFolder, $"Results_{run.Environment}_{run.RunId}.xlsx");
        xl.Save(file);
        return file;
    }

    public static string WriteDiscover(RunResult run, IReadOnlyDictionary<string, StepResponse> responses, TestSuite suite, string outputFolder)
    {
        var mask = new Masker(suite);
        var xl = new XlsxWriter();
        var sheet = xl.AddSheet("ResponseFields").ColumnWidths(24, 6, 26, 60, 40, 16).Freeze(1);
        sheet.AddRow(H("Scenario"), H("Step"), H("Step Name"), H("Field Path"), H("Sample Value"), H("Type"));
        foreach (var s in run.Scenarios.SelectMany(x => x.Steps))
        {
            if (!responses.TryGetValue(s.Def.Key, out var resp) || resp is null) continue;
            if (resp.HttpStatus is int hs) sheet.AddRow(s.Def.Scenario, s.Def.Step, s.Def.StepName, "HTTP Status", hs.ToString(), "number");
            foreach (var (cn, cv) in resp.Cookies)
                sheet.AddRow(s.Def.Scenario, s.Def.Step, s.Def.StepName, $"Cookie:{cn}", mask.Value(cv), "cookie");
            if (resp.BodyJson is null)
            {
                sheet.AddRow(s.Def.Scenario, s.Def.Step, s.Def.StepName, "Body", XCell.S(resp.BodyText, CellStyle.Wrap), "text (not JSON)");
                continue;
            }
            foreach (var (path, value, kind) in JsonPathUtil.Flatten(resp.BodyJson))
            {
                var leaf = Regex.Match(path, @"([A-Za-z0-9_]+)$").Value;
                sheet.AddRow(s.Def.Scenario, s.Def.Step, s.Def.StepName, path, XCell.S(mask.ShouldMask(leaf) && kind != "null" ? mask.Value(value) : value, CellStyle.Wrap), kind);
            }
        }
        sheet.Filter(1, 6);

        var help = xl.AddSheet("How to use");
        help.ColumnWidths(110);
        help.AddRow(XCell.S("How to use this file", CellStyle.Title));
        help.AddRow("1. Find the field you want to check in the ResponseFields sheet (use the filter on the header row).");
        help.AddRow("2. Copy the 'Field Path' cell into the 'Field Path' column of the Assertions (or Capture) sheet in your test workbook.");
        help.AddRow("3. Choose a Check from the dropdown and type the Expected value.");
        help.AddRow("Tip: values that change every call (RequestId, RequestAt, ResponseAt, reference numbers) should use 'is not empty' or 'matches pattern', not 'equals'.");

        var file = Path.Combine(outputFolder, $"Discover_{run.Environment}_{run.RunId}.xlsx");
        xl.Save(file);
        return file;
    }

    private static XCell H(string s) => XCell.S(s, CellStyle.Header);

    private static XCell Result(Outcome o) => o switch
    {
        Outcome.Pass => XCell.S("PASS", CellStyle.Pass),
        Outcome.Fail => XCell.S("FAIL", CellStyle.Fail),
        Outcome.Error => XCell.S("ERROR", CellStyle.Fail),
        _ => XCell.S("SKIPPED", CellStyle.Skipped),
    };

    private static string Safe(string s) => Regex.Replace(s, @"[^A-Za-z0-9_\-]", "_");
}
