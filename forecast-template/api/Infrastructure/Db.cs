using Npgsql;
using NpgsqlTypes;

namespace Forecast.Api.Infrastructure;

// TODO(EF Core): заглушка работает на «голом» Npgsql. Когда подключим EF Core, CRUD-запросы переедут в DbContext,
// а COPY-импорт и запись прогнозов останутся на NpgsqlBinaryImporter.
public static class Db
{
    public static NpgsqlCommand Cmd(this NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null)
        => new(sql, conn, tx);

    public static NpgsqlCommand P(this NpgsqlCommand cmd, string name, object? value, NpgsqlDbType type)
    {
        cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        return cmd;
    }

    public static T? Val<T>(this NpgsqlDataReader r, int i) where T : struct
        => r.IsDBNull(i) ? null : r.GetFieldValue<T>(i);

    public static string? Str(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static async Task<DatasetRow?> GetDatasetAsync(this NpgsqlDataSource ds, Guid? id, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = id is { } dsId
            ? conn.Cmd("SELECT id, name, status, rows, series_count, date_from, date_to FROM datasets WHERE id = @id")
                  .P("id", dsId, NpgsqlDbType.Uuid)
            : conn.Cmd("""
                SELECT id, name, status, rows, series_count, date_from, date_to FROM datasets
                WHERE status = 'ready' ORDER BY ready_at DESC NULLS LAST LIMIT 1
                """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DatasetRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Val<long>(3), r.Val<int>(4),
            r.Val<DateOnly>(5), r.Val<DateOnly>(6));
    }

    public static async Task<Guid?> GetLatestReadyDatasetIdAsync(this NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(
            "SELECT id FROM datasets WHERE status = 'ready' ORDER BY ready_at DESC NULLS LAST LIMIT 1");
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }
}

public sealed record DatasetRow(Guid Id, string Name, string Status, long? Rows, int? SeriesCount,
    DateOnly? DateFrom, DateOnly? DateTo);
