using System.Text.Json;
using Forecast.Api.Import;
using Forecast.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Forecast.Api.Endpoints;

/// <summary>POST /api/datasets/import, GET /api/jobs/{jobId}, GET /api/meta.</summary>
public static class DatasetEndpoints
{
    public static void MapDatasets(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapPost("/datasets/import", Import).WithTags("datasets").WithName("importDataset");
        api.MapGet("/jobs/{jobId:guid}", GetJob).WithTags("jobs").WithName("getJob");
        api.MapGet("/meta", GetMeta).WithTags("meta").WithName("getMeta");
        // TODO(день 2+): POST /api/datasets (multipart, x-stub: false) — тот же ImportWorker, но из временного файла.
    }

    private static async Task<IResult> Import(ImportDatasetRequest req, HttpContext http, NpgsqlDataSource ds,
        IOptions<ImportOptions> opts, WorkQueue<ImportWork> queue, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Path)) return Problems.Validation("path", "Обязательное поле");
        var rel = req.Path.Replace('\\', '/');
        if (Path.IsPathRooted(rel) || rel.StartsWith('/') || rel.Split('/').Any(s => s == ".."))
            return Problems.Validation("path", "Нужен относительный путь внутри каталога импорта, без '..'");

        var root = Path.GetFullPath(opts.Value.Root);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Problems.Validation("path", "Путь выходит за каталог импорта");
        if (!File.Exists(full)) return Problems.NotFound($"Файл '{rel}' не найден в каталоге импорта");

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid datasetId;
        bool isNew = req.DatasetId is null;
        if (req.DatasetId is { } existing)
        {
            await using var check = conn.Cmd("SELECT 1 FROM datasets WHERE id = @id FOR UPDATE", tx)
                .P("id", existing, NpgsqlDbType.Uuid);
            if (await check.ExecuteScalarAsync(ct) is null) return Problems.NotFound($"Датасет {existing} не найден");
            datasetId = existing;
        }
        else
        {
            var name = string.IsNullOrWhiteSpace(req.Name)
                ? Path.GetFileName(Path.GetDirectoryName(full)) is { Length: > 0 } dir ? dir : Path.GetFileNameWithoutExtension(full)
                : req.Name.Trim();
            await using var ins = conn.Cmd("INSERT INTO datasets (name) VALUES (@name) RETURNING id", tx)
                .P("name", name, NpgsqlDbType.Text);
            datasetId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
        }

        await using var job = conn.Cmd("""
            INSERT INTO jobs (kind, dataset_id, params) VALUES ('import', @ds, @params) RETURNING id
            """, tx)
            .P("ds", datasetId, NpgsqlDbType.Uuid)
            .P("params", JsonSerializer.Serialize(new { path = rel, name = req.Name, datasetId = req.DatasetId }), NpgsqlDbType.Jsonb);
        var jobId = (Guid)(await job.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);

        await queue.EnqueueAsync(new ImportWork(jobId, datasetId, full, isNew, Correlation.Get(http)), ct);
        return TypedResults.Accepted($"/api/jobs/{jobId}", new ImportAccepted(datasetId, jobId));
    }

    private static async Task<IResult> GetJob(Guid jobId, NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT id, kind, status, progress, message, dataset_id, result, warnings, error, created_at, started_at, finished_at
            FROM jobs WHERE id = @id
            """);
        cmd.P("id", jobId, NpgsqlDbType.Uuid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Problems.NotFound($"Задача {jobId} не найдена");

        long? rows = null; int? seriesCount = null; DateOnly? from = null, to = null;
        if (r.Str(6) is { } resultJson)
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("rows", out var v) && v.ValueKind == JsonValueKind.Number) rows = v.GetInt64();
            if (root.TryGetProperty("seriesCount", out v) && v.ValueKind == JsonValueKind.Number) seriesCount = v.GetInt32();
            if (root.TryGetProperty("dateFrom", out v) && v.ValueKind == JsonValueKind.String) from = DateOnly.Parse(v.GetString()!);
            if (root.TryGetProperty("dateTo", out v) && v.ValueKind == JsonValueKind.String) to = DateOnly.Parse(v.GetString()!);
        }
        var warnings = JsonSerializer.Deserialize<List<ImportWarning>>(r.GetString(7), JsonSerializerOptions.Web) ?? [];
        float? progress = r.Val<float>(3);

        return TypedResults.Ok(new JobDto(r.GetGuid(0), r.GetString(1), r.GetString(2),
            progress is null ? null : Math.Round(progress.Value, 3), r.Str(4), r.Val<Guid>(5),
            rows, seriesCount, from, to, warnings, r.Str(8),
            r.GetDateTime(9), r.Val<DateTime>(10), r.Val<DateTime>(11)));
    }

    private static async Task<IResult> GetMeta(Guid? datasetId, NpgsqlDataSource ds, CancellationToken ct)
    {
        var latestId = await ds.GetLatestReadyDatasetIdAsync(ct);
        var dataset = datasetId is null ? (latestId is null ? null : await ds.GetDatasetAsync(latestId, ct))
                                        : await ds.GetDatasetAsync(datasetId, ct);
        if (datasetId is not null && dataset is null) return Problems.NotFound($"Датасет {datasetId} не найден");
        if (dataset is null)
            return TypedResults.Ok(new MetaDto(null, null, [], [], 0, null, null, Contract.MaxHorizon,
                Contract.IntervalLevel, null, null));

        await using var conn = await ds.OpenConnectionAsync(ct);
        var stores = new List<StoreDto>();
        await using (var cmd = conn.Cmd("""
                         SELECT DISTINCT ON (store_nbr) store_nbr, attrs->>'city', attrs->>'state', attrs->>'type',
                                CASE WHEN jsonb_typeof(attrs->'cluster') = 'number' THEN (attrs->>'cluster')::int END
                         FROM series WHERE dataset_id = @ds ORDER BY store_nbr
                         """).P("ds", dataset.Id, NpgsqlDbType.Uuid))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                stores.Add(new StoreDto(r.GetInt32(0), r.Str(1), r.Str(2), r.Str(3), r.Val<int>(4)));

        var families = new List<string>();
        await using (var cmd = conn.Cmd("SELECT DISTINCT family FROM series WHERE dataset_id = @ds")
                         .P("ds", dataset.Id, NpgsqlDbType.Uuid))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) families.Add(r.GetString(0));
        families.Sort(StringComparer.Ordinal);

        Guid? runId = null; string? modelVersion = null;
        await using (var cmd = conn.Cmd("""
                         SELECT id, model_version FROM forecast_runs WHERE dataset_id = @ds AND status = 'done'
                         ORDER BY finished_at DESC LIMIT 1
                         """).P("ds", dataset.Id, NpgsqlDbType.Uuid))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { runId = r.GetGuid(0); modelVersion = r.Str(1); }

        return TypedResults.Ok(new MetaDto(dataset.Id, latestId, stores, families, dataset.SeriesCount ?? 0,
            dataset.DateFrom, dataset.DateTo, Contract.MaxHorizon, Contract.IntervalLevel, runId, modelVersion));
    }
}
