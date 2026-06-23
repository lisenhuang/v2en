using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace v2en.Data;

/// <summary>
/// Sets PRAGMA busy_timeout on every opened SQLite connection. busy_timeout is a
/// per-connection setting (it does NOT persist in the DB file like journal_mode=WAL),
/// and EF Core's CommandTimeout does not configure it — so we apply it here.
/// </summary>
public sealed class BusyTimeoutInterceptor : DbConnectionInterceptor
{
    private const string Pragma = "PRAGMA busy_timeout=5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await ApplyAsync(connection, cancellationToken);

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
