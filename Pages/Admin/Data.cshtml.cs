using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;

namespace v2en.Pages.Admin;

/// <summary>
/// Read-only database browser: lists every table and pages through its rows. Generic over the
/// SQLite schema (driven by sqlite_master / PRAGMA table_info), so new tables show up automatically
/// without code changes. Strictly read-only — there is no write path on this page.
///
/// Two safety rules matter here:
///   • The table name is validated against the live table list before it ever reaches SQL, so the
///     identifier we interpolate can only be one SQLite itself reported (no injection surface).
///   • Secret-bearing columns (password hashes, stored API keys) are redacted, never rendered.
/// </summary>
public class DataModel : PageModel
{
    private readonly AppDbContext _db;

    public DataModel(AppDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)] public string? Table { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public const int PageSize = 50;

    public sealed record TableInfo(string Name, long Rows);
    /// <summary>One cell's display state. Null and redacted are rendered distinctly from plain text.</summary>
    public sealed record Cell(string Text, bool IsNull, bool Redacted);

    public List<TableInfo> Tables { get; private set; } = new();
    public List<string> Columns { get; private set; } = new();
    public List<Cell[]> Rows { get; private set; } = new();

    public long Total { get; private set; }
    public int TotalPages { get; private set; }
    public long FirstRow { get; private set; }
    public long LastRow { get; private set; }

    /// <summary>Columns whose values must never be shown (hashes, API keys). Keyed by table name.</summary>
    private static readonly Dictionary<string, HashSet<string>> Redacted = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AdminUsers"] = new(StringComparer.OrdinalIgnoreCase) { "PasswordHash" },
        ["RuntimeSettings"] = new(StringComparer.OrdinalIgnoreCase) { "OpenRouterApiKey", "GeminiEmbedKeysJson" },
    };

    private const int MaxCellChars = 400;   // long blobs/JSON/text are clipped for display only

    public async Task OnGetAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        Tables = await LoadTablesAsync(conn, ct);
        if (Tables.Count == 0) return;

        // Validate the requested table against the real list — anything else falls back to the first.
        var selected = Tables.FirstOrDefault(t => string.Equals(t.Name, Table, StringComparison.Ordinal))?.Name
                       ?? Tables[0].Name;
        Table = selected;

        Total = Tables.First(t => t.Name == selected).Rows;
        TotalPages = Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
        P = Math.Clamp(P, 1, TotalPages);
        var offset = (long)(P - 1) * PageSize;

        var qTable = Quote(selected);
        var orderBy = await BuildOrderByAsync(conn, selected, ct);
        var sensitive = Redacted.TryGetValue(selected, out var s) ? s : null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT * FROM {qTable}{orderBy} LIMIT $take OFFSET $skip";
            AddParam(cmd, "$take", PageSize);
            AddParam(cmd, "$skip", offset);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            for (int i = 0; i < reader.FieldCount; i++)
                Columns.Add(reader.GetName(i));

            while (await reader.ReadAsync(ct))
            {
                var row = new Cell[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (sensitive is not null && sensitive.Contains(reader.GetName(i)))
                        row[i] = new Cell("", false, true);
                    else if (await reader.IsDBNullAsync(i, ct))
                        row[i] = new Cell("", true, false);
                    else
                        row[i] = new Cell(Format(reader.GetValue(i)), false, false);
                }
                Rows.Add(row);
            }
        }

        FirstRow = Rows.Count == 0 ? 0 : offset + 1;
        LastRow = offset + Rows.Count;
    }

    /// <summary>Every user table (skips SQLite's internal tables), each with its current row count.</summary>
    private static async Task<List<TableInfo>> LoadTablesAsync(DbConnection conn, CancellationToken ct)
    {
        var names = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                names.Add(reader.GetString(0));
        }

        var tables = new List<TableInfo>(names.Count);
        foreach (var name in names)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(name)}";
            var count = await cmd.ExecuteScalarAsync(ct);
            tables.Add(new TableInfo(name, Convert.ToInt64(count)));
        }
        return tables;
    }

    /// <summary>Order newest-first by the primary key (descending) when the table has one.</summary>
    private static async Task<string> BuildOrderByAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var pk = new List<(int Seq, string Name)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // table_info columns: cid, name, type, notnull, dflt_value, pk
            var name = reader.GetString(1);
            var pkPos = reader.GetInt32(5);
            if (pkPos > 0) pk.Add((pkPos, name));
        }
        if (pk.Count == 0) return "";
        var cols = pk.OrderBy(c => c.Seq).Select(c => $"{Quote(c.Name)} DESC");
        return " ORDER BY " + string.Join(", ", cols);
    }

    /// <summary>Renders a raw SQLite value for display: blobs by size, long text clipped.</summary>
    private static string Format(object value)
    {
        if (value is byte[] blob)
            return $"(blob, {blob.Length} bytes)";
        var s = value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (s.Length > MaxCellChars)
            return s[..MaxCellChars] + $"… (+{s.Length - MaxCellChars} chars)";
        return s;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>Quote a SQLite identifier (table/column), escaping any embedded double-quote.</summary>
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
