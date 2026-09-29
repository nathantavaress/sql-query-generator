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

// Gera a query (texto SQL) para o usuário copiar
app.MapPost("/api/query/generate", IResult (QueryRequest req) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var built = QueryBuilder.Build(conn, req);
    return built.Error is null
        ? Results.Ok(new { sql = built.Sql })
        : Results.BadRequest(new { error = built.Error });
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
    List<SortDto>? Sort);
record BuildResult(string? Sql, string? Error);

// ---------- Gerador ----------
static class QueryBuilder
{
    static readonly Dictionary<string, string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = "=",
        ["<>"] = "<>",
        [">"] = ">",
        ["<"] = "<",
        [">="] = ">=",
        ["<="] = "<=",
        ["LIKE"] = "LIKE",
        ["NOT LIKE"] = "NOT LIKE",
        ["CONTAINS"] = "LIKE",
        ["IS NULL"] = "IS NULL",
        ["IS NOT NULL"] = "IS NOT NULL"
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
        var sb = new StringBuilder($"SELECT {select}\nFROM {Quote(req.Table)}");

        // WHERE
        var conditions = new List<string>();
        foreach (var filter in req.Filters ?? new List<FilterDto>())
        {
            if (!schema.TryGetValue(filter.Column ?? "", out var type))
                return Fail($"Coluna inválida no filtro: {filter.Column}");
            if (!Operators.TryGetValue((filter.Op ?? "").Trim(), out var sqlOperator))
                return Fail($"Operador inválido: {filter.Op}");

            var col = Quote(filter.Column!);
            if (sqlOperator.StartsWith("IS"))
            {
                conditions.Add($"{col} {sqlOperator}");
                continue;
            }
            if (string.IsNullOrEmpty(filter.Value))
                return Fail($"Informe um valor para o filtro em {filter.Column}.");

            var value = filter.Op!.Trim().Equals("CONTAINS", StringComparison.OrdinalIgnoreCase)
                ? $"%{filter.Value}%" : filter.Value!;
            conditions.Add($"{col} {sqlOperator} {Literal(value, type)}");
        }
        var logic = string.Equals(req.Logic, "OR", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
        if (conditions.Count > 0)
            sb.Append("\nWHERE ").Append(string.Join(logic, conditions));

        // ORDER BY
        var order = new List<string>();
        foreach (var s in req.Sort ?? new List<SortDto>())
        {
            if (!schema.ContainsKey(s.Column ?? "")) return Fail($"Coluna inválida na ordenação: {s.Column}");
            var dir = string.Equals(s.Direction, "DESC", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            order.Add($"{Quote(s.Column!)} {dir}");
        }
        if (order.Count > 0)
            sb.Append("\nORDER BY ").Append(string.Join(", ", order));

        return new BuildResult(sb.Append(';').ToString(), null);
    }

    static BuildResult Fail(string msg) => new(null, msg);

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

    // Número vira número, o resto vira texto entre aspas (aspas simples são dobradas)
    static string Literal(string value, string type)
    {
        var t = type.ToUpperInvariant();
        bool numeric = t.Contains("INT") || t.Contains("NUMERIC") || t.Contains("REAL")
                    || t.Contains("FLOA") || t.Contains("DOUB") || t.Contains("DEC");
        if (numeric && Regex.IsMatch(value.Trim(), @"^-?\d+(\.\d+)?$")) return value.Trim();
        return "'" + value.Replace("'", "''") + "'";
    }
}