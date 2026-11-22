using System.Diagnostics;
using System.Text.Json.Nodes;
#if ENABLE_SQL
using Microsoft.Data.SqlClient;
#endif

namespace ApiTestRunner;

/// <summary>
/// Optional "Method = SQL" step for verifying what the API wrote to the database.
/// The Environments row for the step's Service holds the connection string.
/// {{placeholders}} in the query become SQL parameters (no string concatenation, no injection).
/// Result shape for assertions:  RowCount,  Rows[0].ColumnName
///
/// Needs Microsoft.Data.SqlClient, so it is only compiled with:  dotnet build -p:EnableSql=true
/// </summary>
public static class SqlStep
{
    public static async Task<StepResponse> ExecuteAsync(StepExecutor.PreparedRequest req, VariableContext vars, CancellationToken ct)
    {
#if ENABLE_SQL
        var parameters = new List<(string Name, string Value)>();
        var unresolved = new List<string>();
        var sql = VariableContext.PlaceholderRegex.Replace(req.Body ?? "", m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (!vars.TryGet(name, out var value)) { unresolved.Add(name); return m.Value; }
            var p = "@p" + parameters.Count;
            parameters.Add((p, value));
            return p;
        });
        if (unresolved.Count > 0)
            throw new StepConfigException($"Unknown variable(s) in SQL: {string.Join(", ", unresolved.Select(u => "{{" + u + "}}"))}");

        var sw = Stopwatch.StartNew();
        await using var conn = new SqlConnection(req.Url);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);

        var rows = new JsonArray();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var row = new JsonObject();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var v = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[reader.GetName(i)] = v switch
                    {
                        null => null,
                        DateTime dt => JsonValue.Create(dt.ToString("yyyy-MM-dd HH:mm:ss.fff")),
                        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
                        _ => JsonValue.Create(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)),
                    };
                }
                rows.Add(row);
            }
        }
        sw.Stop();
        var result = new JsonObject { ["RowCount"] = rows.Count, ["Rows"] = rows };
        var text = result.ToJsonString(JsonPathUtil.Pretty);
        return new StepResponse { DurationMs = sw.ElapsedMilliseconds, BodyText = text, BodyJson = JsonPathUtil.TryParse(text) };
#else
        await Task.CompletedTask;
        throw new StepConfigException("SQL steps are not enabled in this build. Rebuild with:  dotnet build -p:EnableSql=true  (needs the Microsoft.Data.SqlClient NuGet package).");
#endif
    }
}
