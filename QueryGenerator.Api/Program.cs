using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dbPath = Path.Combine(app.Environment.ContentRootPath, "Data", "Chinook.sqlite");
var connectionString = $"Data Source={dbPath};Mode=ReadOnly";

app.UseDefaultFiles();
app.UseStaticFiles();

// Lista as tabelas do banco
app.MapGet("/api/tables", () =>
{
    var tables = new List<string>();
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) tables.Add(reader.GetString(0));
    return Results.Ok(tables);
});

// Lista as colunas de uma tabela
app.MapGet("/api/tables/{table}/columns", IResult (string table) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    using var check = conn.CreateCommand();
    check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = $name";
    check.Parameters.AddWithValue("$name", table);
    if ((long)check.ExecuteScalar()! == 0) return Results.NotFound();

    var columns = new List<object>();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        columns.Add(new { name = reader.GetString(1), type = reader.GetString(2) });
    return Results.Ok(columns);
});

// Só gera a query (texto SQL) para o usuário copiar
app.MapPost("/api/query/generate", IResult (QueryRequest req) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var built = QueryBuilder.Build(conn, req);
    return built.Error is null
        ? Results.Ok(new { sql = built.DisplaySql })
        : Results.BadRequest(new { error = built.Error });
});

// Gera e executa a query, devolvendo o resultado
app.MapPost("/api/query/execute", IResult (QueryRequest req) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    var limited = req with { Limit = Math.Clamp(req.Limit ?? 100, 1, 1000) };
    var built = QueryBuilder.Build(conn, limited);
    if (built.Error is not null) return Results.BadRequest(new { error = built.Error });

    using var cmd = conn.CreateCommand();
    cmd.CommandText = built.ExecSql;
    foreach (var (name, value) in built.Params)
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    using var reader = cmd.ExecuteReader();
    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
    var rows = new List<object?[]>();
    while (reader.Read())
    {
        var row = new object?[reader.FieldCount];
        for (int i = 0; i < row.Length; i++)
            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        rows.Add(row);
    }
    return Results.Ok(new { sql = built.DisplaySql, columns, rows });
});

app.Run();

// ---------- Modelos ----------
record FilterDto(string Column, string Op, string? Value);
record SortDto(string Column, string? Direction);
record QueryRequest(
    string Table,
    List<string>? Columns,
    List<FilterDto>? Filters,
    string? Logic,          // "AND" (padrão) ou "OR"
    List<SortDto>? Sort,
    int? Limit);
record BuildResult(string? ExecSql, string? DisplaySql, Dictionary<string, object?> Params, string? Error);

// ---------- Gerador ----------
static class QueryBuilder
{
    static readonly Dictionary<string, string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = "=", ["<>"] = "<>", [">"] = ">", ["<"] = "<", [">="] = ">=", ["<="] = "<=",
        ["LIKE"] = "LIKE", ["NOT LIKE"] = "NOT LIKE", ["CONTAINS"] = "LIKE",
        ["IS NULL"] = "IS NULL", ["IS NOT NULL"] = "IS NOT NULL"
    };

    public static BuildResult Build(SqliteConnection conn, QueryRequest req)
    {
        var schema = GetSchema(conn, req.Table);
        if (schema is null) return Fail("Tabela não encontrada.");

        // SELECT
        var cols = req.Columns is { Count: > 0 } ? req.Columns : null;
        if (cols is not null)
            foreach (var c in cols)
                if (!schema.ContainsKey(c)) return Fail($"Coluna inválida: {c}");
        var select = cols is null ? "*" : string.Join(", ", cols.Select(Quote));
        var head = $"SELECT {select}\nFROM {Quote(req.Table)}";

        // WHERE
        var ps = new Dictionary<string, object?>();
        var execConds = new List<string>();
        var dispConds = new List<string>();
        int i = 0;
        foreach (var f in req.Filters ?? new List<FilterDto>())
        {
            if (!schema.TryGetValue(f.Column ?? "", out var type))
                return Fail($"Coluna inválida no filtro: {f.Column}");
            if (!Operators.TryGetValue((f.Op ?? "").Trim(), out var op))
                return Fail($"Operador inválido: {f.Op}");

            var col = Quote(f.Column!);
            if (op.StartsWith("IS"))
            {
                execConds.Add($"{col} {op}");
                dispConds.Add($"{col} {op}");
                continue;
            }
            if (string.IsNullOrEmpty(f.Value))
                return Fail($"Informe um valor para o filtro em {f.Column}.");

            var value = f.Op!.Trim().Equals("CONTAINS", StringComparison.OrdinalIgnoreCase)
                ? $"%{f.Value}%" : f.Value!;
            var p = $"$p{i++}";
            ps[p] = value;
            execConds.Add($"{col} {op} {p}");
            dispConds.Add($"{col} {op} {Literal(value, type)}");
        }
        var logic = string.Equals(req.Logic, "OR", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";

        // ORDER BY
        var order = new List<string>();
        foreach (var s in req.Sort ?? new List<SortDto>())
        {
            if (!schema.ContainsKey(s.Column ?? "")) return Fail($"Coluna inválida na ordenação: {s.Column}");
            var dir = string.Equals(s.Direction, "DESC", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            order.Add($"{Quote(s.Column!)} {dir}");
        }

        string Assemble(List<string> conds)
        {
            var sb = new StringBuilder(head);
            if (conds.Count > 0) sb.Append("\nWHERE ").Append(string.Join(logic, conds));
            if (order.Count > 0) sb.Append("\nORDER BY ").Append(string.Join(", ", order));
            if (req.Limit is > 0) sb.Append("\nLIMIT ").Append(req.Limit);
            return sb.Append(';').ToString();
        }

        return new BuildResult(Assemble(execConds), Assemble(dispConds), ps, null);
    }

    static BuildResult Fail(string msg) => new(null, null, new(), msg);

    // Nome -> tipo das colunas; null se a tabela não existir
    static Dictionary<string, string>? GetSchema(SqliteConnection conn, string table)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = $name";
        check.Parameters.AddWithValue("$name", table ?? "");
        if ((long)check.ExecuteScalar()! == 0) return null;

        var schema = new Dictionary<string, string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table!)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) schema[reader.GetString(1)] = reader.GetString(2);
        return schema;
    }

    static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    // Só para exibição: número vira número, o resto vira texto entre aspas
    static string Literal(string value, string type)
    {
        var t = type.ToUpperInvariant();
        bool numeric = t.Contains("INT") || t.Contains("NUMERIC") || t.Contains("REAL")
                    || t.Contains("FLOA") || t.Contains("DOUB") || t.Contains("DEC");
        if (numeric && Regex.IsMatch(value.Trim(), @"^-?\d+(\.\d+)?$")) return value.Trim();
        return "'" + value.Replace("'", "''") + "'";
    }
}