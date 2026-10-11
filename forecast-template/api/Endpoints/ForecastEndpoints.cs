using System.Globalization;
using System.Text;
using System.Text.Json;
using Forecast.Api.Forecasts;
using Forecast.Api.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Forecast.Api.Endpoints;

/// <summary>POST /api/forecasts, GET /api/forecasts/{runId}, GET /api/forecasts/latest/series, GET /api/forecasts/{runId}/export.csv.</summary>
public static class ForecastEndpoints
{
    public static void MapForecasts(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/forecasts").WithTags("forecasts");
        g.MapPost("", Create).WithName("createForecast");
        g.MapGet("/latest/series", LatestSeries).WithName("getLatestForecastSeries");
        g.MapGet("/{runId:guid}", GetRun).WithName("getForecastRun");
        g.MapGet("/{runId:guid}/export.csv", Export).WithName("exportForecastCsv");
    }

    private static async Task<IResult> Create(CreateForecastRequest? req, HttpContext http, NpgsqlDataSource ds,
        WorkQueue<ForecastWork> queue, CancellationToken ct)
    {
        req ??= new CreateForecastRequest(null, null, null);
        var horizon = req.Horizon ?? Contract.MaxHorizon;
        if (horizon is < 1 or > Contract.MaxHorizon) return Problems.Validation("horizon", "Должен быть от 1 до 16");

        var dataset = await ds.GetDatasetAsync(req.DatasetId, ct);
        if (dataset is null)
            return Problems.NotFound(req.DatasetId is null ? "Нет ни одного готового датасета" : $"Датасет {req.DatasetId} не найден");
        if (dataset.Status != "ready" || dataset.DateTo is null)
            return Problems.Validation("datasetId", $"Датасет в статусе '{dataset.Status}' или без истории");

        var cutoff = req.CutoffDate ?? dataset.DateTo.Value;
        if (cutoff < dataset.DateFrom || cutoff > dataset.DateTo)
            return Problems.Validation("cutoffDate", $"Должна лежать в [{dataset.DateFrom:yyyy-MM-dd}, {dataset.DateTo:yyyy-MM-dd}]");

        await using var cmd = ds.CreateCommand("""
            INSERT INTO forecast_runs (dataset_id, horizon, cutoff_date, interval_level) VALUES (@ds, @h, @c, @i) RETURNING id
            """);
        cmd.P("ds", dataset.Id, NpgsqlDbType.Uuid).P("h", horizon, NpgsqlDbType.Integer)
           .P("c", cutoff, NpgsqlDbType.Date).P("i", (float)Contract.IntervalLevel, NpgsqlDbType.Real);
        var runId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await queue.EnqueueAsync(new ForecastWork(runId, Correlation.Get(http)), ct);
        return TypedResults.Accepted($"/api/forecasts/{runId}", new ForecastAccepted(runId, "queued"));
    }

    private static async Task<IResult> GetRun(Guid runId, NpgsqlDataSource ds, CancellationToken ct)
    {
        var run = await LoadRunAsync(ds, runId, ct);
        return run is null ? Problems.NotFound($"Прогон {runId} не найден") : TypedResults.Ok(run);
    }

    private static async Task<ForecastRunDto?> LoadRunAsync(NpgsqlDataSource ds, Guid runId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT id, dataset_id, status, horizon, cutoff_date, model_version, interval_level, metrics, error,
                   created_at, started_at, finished_at
            FROM forecast_runs WHERE id = @id
            """);
        cmd.P("id", runId, NpgsqlDbType.Uuid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var metrics = r.Str(7) is { } m ? JsonSerializer.Deserialize<MetricsDto>(m, JsonSerializerOptions.Web) : null;
        return new ForecastRunDto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetFieldValue<DateOnly>(4),
            r.Str(5), Math.Round(r.GetFloat(6), 2), metrics, r.Str(8), r.GetDateTime(9), r.Val<DateTime>(10), r.Val<DateTime>(11));
    }

    private static async Task<IResult> LatestSeries(NpgsqlDataSource ds, CancellationToken ct,
        string? store = "all", string? family = "all", int? horizon = null, int? historyDays = null, Guid? runId = null)
    {
        store = string.IsNullOrWhiteSpace(store) ? "all" : store.Trim();
        family = string.IsNullOrWhiteSpace(family) ? "all" : family;
        int? storeNbr = null;
        if (store != "all")
        {
            if (!int.TryParse(store, NumberStyles.None, CultureInfo.InvariantCulture, out var s))
                return Problems.Validation("store", "Номер магазина или 'all'");
            storeNbr = s;
        }
        string? fam = family == "all" ? null : family;
        if (horizon is < 1 or > Contract.MaxHorizon) return Problems.Validation("horizon", "Должен быть от 1 до 16");
        if (historyDays is < 1) return Problems.Validation("historyDays", "Должен быть >= 1");

        ForecastRunDto? run = null;
        DatasetRow? dataset;
        if (runId is { } rid)
        {
            run = await LoadRunAsync(ds, rid, ct);
            if (run is null) return Problems.NotFound($"Прогон {rid} не найден");
            dataset = await ds.GetDatasetAsync(run.DatasetId, ct);
        }
        else
        {
            dataset = await ds.GetDatasetAsync(null, ct);
            if (dataset is null) return Problems.NotFound("Нет ни одного готового датасета");
            await using var cmd = ds.CreateCommand("""
                SELECT id FROM forecast_runs WHERE dataset_id = @ds AND status = 'done' ORDER BY finished_at DESC LIMIT 1
                """);
            cmd.P("ds", dataset.Id, NpgsqlDbType.Uuid);
            if (await cmd.ExecuteScalarAsync(ct) is Guid latest) run = await LoadRunAsync(ds, latest, ct);
        }
        if (dataset is null) return Problems.NotFound("Датасет прогона не найден");

        await using var conn = await ds.OpenConnectionAsync(ct);

        // Существует ли такой ряд/срез.
        await using (var exists = conn.Cmd("""
                         SELECT EXISTS (SELECT 1 FROM series WHERE dataset_id = @ds
                                        AND (@store::int IS NULL OR store_nbr = @store)
                                        AND (@fam::text IS NULL OR family = @fam))
                         """)
                         .P("ds", dataset.Id, NpgsqlDbType.Uuid).P("store", storeNbr, NpgsqlDbType.Integer)
                         .P("fam", fam, NpgsqlDbType.Text))
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
                return Problems.NotFound($"Ряд store={store}, family={family} не найден в датасете");

        var cutoff = run?.CutoffDate ?? dataset.DateTo;

        var history = new List<HistoryPoint>();
        await using (var cmd = conn.Cmd("""
                         SELECT o.date, sum(o.sales), sum(o.onpromotion)
                         FROM observations o JOIN series s ON s.id = o.series_id
                         WHERE s.dataset_id = @ds AND o.sales IS NOT NULL
                           AND (@store::int IS NULL OR s.store_nbr = @store)
                           AND (@fam::text IS NULL OR s.family = @fam)
                           AND (@cutoff::date IS NULL OR o.date <= @cutoff)
                           AND (@from::date IS NULL OR o.date > @from)
                         GROUP BY o.date ORDER BY o.date
                         """)
                         .P("ds", dataset.Id, NpgsqlDbType.Uuid).P("store", storeNbr, NpgsqlDbType.Integer)
                         .P("fam", fam, NpgsqlDbType.Text).P("cutoff", cutoff, NpgsqlDbType.Date)
                         .P("from", historyDays is { } hd && cutoff is { } c ? c.AddDays(-hd) : null, NpgsqlDbType.Date))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                history.Add(new HistoryPoint(r.GetFieldValue<DateOnly>(0), r.GetDouble(1), r.Val<long>(2)));

        var forecast = new List<ForecastPoint>();
        if (run is not null && run.Status == "done")
        {
            var maxDate = run.CutoffDate.AddDays(Math.Min(horizon ?? run.Horizon, run.Horizon));
            bool single = storeNbr is not null && fam is not null;
            bool full = storeNbr is null && fam is null;
            if (full)
            {
                // Полный агрегат приходит от ML (series_id IS NULL), с интервалом.
                await using var cmd = conn.Cmd("""
                    SELECT date, yhat, lo, hi FROM forecasts
                    WHERE run_id = @run AND series_id IS NULL AND date <= @max ORDER BY date
                    """).P("run", run.RunId, NpgsqlDbType.Uuid).P("max", maxDate, NpgsqlDbType.Date);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    forecast.Add(new ForecastPoint(r.GetFieldValue<DateOnly>(0), r.GetDouble(1), r.Val<double>(2), r.Val<double>(3)));
            }
            if (forecast.Count == 0)
            {
                // Один ряд — с интервалом; частичный агрегат (и полный, если ML не прислал aggregate) — сумма без интервала.
                await using var cmd = conn.Cmd("""
                    SELECT f.date, sum(f.yhat),
                           CASE WHEN @single THEN max(f.lo) END, CASE WHEN @single THEN max(f.hi) END
                    FROM forecasts f JOIN series s ON s.id = f.series_id
                    WHERE f.run_id = @run AND s.dataset_id = @ds AND f.date <= @max
                      AND (@store::int IS NULL OR s.store_nbr = @store)
                      AND (@fam::text IS NULL OR s.family = @fam)
                    GROUP BY f.date ORDER BY f.date
                    """)
                    .P("run", run.RunId, NpgsqlDbType.Uuid).P("ds", dataset.Id, NpgsqlDbType.Uuid)
                    .P("max", maxDate, NpgsqlDbType.Date).P("single", single, NpgsqlDbType.Boolean)
                    .P("store", storeNbr, NpgsqlDbType.Integer).P("fam", fam, NpgsqlDbType.Text);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    forecast.Add(new ForecastPoint(r.GetFieldValue<DateOnly>(0), r.GetDouble(1), r.Val<double>(2), r.Val<double>(3)));
            }
        }

        bool done = run?.Status == "done";
        return TypedResults.Ok(new SeriesForecastDto(done ? run!.RunId : null, dataset.Id, done ? run!.ModelVersion : null,
            store, family, cutoff, Contract.IntervalLevel, history, forecast, done ? run!.Metrics : null,
            done ? run!.FinishedAt : null));
    }

    private static async Task<IResult> Export(Guid runId, HttpContext http, NpgsqlDataSource ds, CancellationToken ct)
    {
        var run = await LoadRunAsync(ds, runId, ct);
        if (run is null) return Problems.NotFound($"Прогон {runId} не найден");
        if (run.Status != "done") return Problems.Conflict($"Прогон в статусе '{run.Status}', выгрузка доступна после done");

        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"forecast-{runId}.csv\"";
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.Cmd("""
            SELECT s.store_nbr, s.family, f.date, f.yhat, f.lo, f.hi
            FROM forecasts f JOIN series s ON s.id = f.series_id
            WHERE f.run_id = @run
            ORDER BY s.store_nbr, s.family, f.date
            """).P("run", runId, NpgsqlDbType.Uuid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await using var w = new StreamWriter(http.Response.Body, new UTF8Encoding(false), 1 << 16);
        await w.WriteAsync("store_nbr,family,date,yhat,lo,hi\n");
        var inv = CultureInfo.InvariantCulture;
        while (await r.ReadAsync(ct))
        {
            var fam = r.GetString(1);
            if (fam.AsSpan().IndexOfAny(",\"\n\r") >= 0) fam = "\"" + fam.Replace("\"", "\"\"") + "\"";
            await w.WriteAsync(string.Create(inv, $"{r.GetInt32(0)},{fam},{r.GetFieldValue<DateOnly>(2):yyyy-MM-dd},{r.GetDouble(3):R},{(r.IsDBNull(4) ? "" : r.GetDouble(4).ToString("R", inv))},{(r.IsDBNull(5) ? "" : r.GetDouble(5).ToString("R", inv))}\n"));
        }
        await w.FlushAsync(ct);
        return Results.Empty;
    }
}
