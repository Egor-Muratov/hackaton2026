using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Forecast.Api.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using Polly.Timeout;
using Serilog.Context;

namespace Forecast.Api.Forecasts;

public sealed record ForecastWork(Guid RunId, string? CorrelationId);

/// <summary>
/// Фоновый прогон: ML /predict по всем рядам (series_keys = null) + агрегат, ответ пишем в forecasts через COPY BINARY.
/// Ошибка ML (в т.ч. тело 422) попадает в forecast_runs.error, run -> failed.
/// </summary>
public sealed class ForecastWorker(
    WorkQueue<ForecastWork> queue,
    NpgsqlDataSource ds,
    MlClient ml,
    ILogger<ForecastWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedAsync(stoppingToken);
        await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using (LogContext.PushProperty("CorrelationId", work.CorrelationId))
            using (LogContext.PushProperty("RunId", work.RunId))
                await RunAsync(work, stoppingToken);
        }
    }

    private async Task FailInterruptedAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE forecast_runs SET status = 'failed', error = 'Прервано рестартом API', finished_at = now()
                WHERE status IN ('queued', 'running')
                """);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Не удалось пометить прерванные прогоны (БД недоступна?)");
        }
    }

    private async Task RunAsync(ForecastWork w, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            Guid datasetId; int horizon; DateOnly cutoff; float interval;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var cmd = conn.Cmd("""
                       UPDATE forecast_runs SET status = 'running', started_at = now() WHERE id = @id
                       RETURNING dataset_id, horizon, cutoff_date, interval_level
                       """).P("id", w.RunId, NpgsqlDbType.Uuid))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return; // run удалён
                datasetId = r.GetGuid(0); horizon = r.GetInt32(1); cutoff = r.GetFieldValue<DateOnly>(2); interval = r.GetFloat(3);
            }

            log.LogInformation("ML /predict: dataset {DatasetId}, cutoff {Cutoff}, horizon {Horizon}", datasetId, cutoff, horizon);
            var resp = await ml.PredictAsync(
                new PredictRequest(datasetId, w.RunId, cutoff, horizon, SeriesKeys: null, IncludeAggregate: true,
                    Interval: Math.Round(interval, 2)),
                w.CorrelationId, ct);
            log.LogInformation("ML ответил за {Seconds:0.0} с: {Points} точек, агрегат {Agg}, модель {Model}",
                sw.Elapsed.TotalSeconds, resp.Forecasts.Count, resp.Aggregate?.Count, resp.ModelVersion);

            await SaveAsync(w.RunId, datasetId, horizon, cutoff, resp, ct);
            log.LogInformation("Прогон {RunId} готов за {Seconds:0.0} с", w.RunId, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            var error = ex switch
            {
                OperationCanceledException when ct.IsCancellationRequested => "Остановлено при выключении API",
                MlException => ex.Message,
                TimeoutRejectedException => $"ML /predict: таймаут ({ex.Message})",
                HttpRequestException => $"ML недоступен: {ex.Message} {ex.InnerException?.Message}".TrimEnd(),
                _ => ex.Message,
            };
            log.LogError(ex, "Прогон {RunId} упал: {Error}", w.RunId, error);
            try
            {
                await using var cmd = ds.CreateCommand(
                    "UPDATE forecast_runs SET status = 'failed', error = @err, finished_at = now() WHERE id = @id");
                cmd.P("id", w.RunId, NpgsqlDbType.Uuid).P("err", error, NpgsqlDbType.Text);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception inner)
            {
                log.LogError(inner, "Не удалось записать статус failed для прогона {RunId}", w.RunId);
            }
        }
    }

    private async Task SaveAsync(Guid runId, Guid datasetId, int horizon, DateOnly cutoff, PredictResponse resp, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        var seriesByKey = new Dictionary<string, int>();
        await using (var cmd = conn.Cmd("SELECT id, store_nbr, family FROM series WHERE dataset_id = @ds")
                         .P("ds", datasetId, NpgsqlDbType.Uuid))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                seriesByKey[SeriesKey.FromDb(r.GetInt32(1), r.GetString(2))] = r.GetInt32(0);

        var warnings = new List<string>(resp.Warnings ?? []);
        foreach (var w in warnings) log.LogInformation("ML warning: {Warning}", w);
        var last = cutoff.AddDays(horizon);
        int outOfRange = 0;

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var del = conn.Cmd("DELETE FROM forecasts WHERE run_id = @id", tx).P("id", runId, NpgsqlDbType.Uuid))
            await del.ExecuteNonQueryAsync(ct); // повтор прогона идемпотентен

        await using (var copy = await conn.BeginBinaryImportAsync(
                         "COPY forecasts (run_id, series_id, date, yhat, lo, hi) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var p in resp.Forecasts)
            {
                if (!seriesByKey.TryGetValue(SeriesKey.FromMl(p.SeriesKey), out var seriesId))
                    throw new InvalidDataException($"ML вернул неизвестный series_key {JsonSerializer.Serialize(p.SeriesKey)}");
                if (p.Date <= cutoff || p.Date > last) { outOfRange++; continue; }
                await WritePoint(copy, runId, seriesId, p.Date, p.Yhat, p.Lower, p.Upper, ct);
            }
            foreach (var a in resp.Aggregate ?? [])
            {
                if (a.Date <= cutoff || a.Date > last) { outOfRange++; continue; }
                await WritePoint(copy, runId, null, a.Date, a.Yhat, a.Lower, a.Upper, ct);
            }
            await copy.CompleteAsync(ct);
        }
        if (outOfRange > 0) warnings.Add($"api_points_out_of_horizon: {outOfRange}");
        if (resp.Aggregate is null) warnings.Add("api_no_aggregate: all/all считается суммой рядов");

        // Метрики храним в camelCase, как отдаём фронту.
        double? M(string k) => resp.CvMetrics?.GetValueOrDefault(k);
        var metrics = JsonSerializer.Serialize(new { rmsle = M("rmsle"), mae = M("mae"), baselineRmsle = M("baseline_rmsle") });

        await using (var upd = conn.Cmd("""
                         UPDATE forecast_runs SET status = 'done', model_version = @mv, metrics = @metrics,
                                                  warnings = @warnings, finished_at = now()
                         WHERE id = @id
                         """, tx)
                         .P("id", runId, NpgsqlDbType.Uuid)
                         .P("mv", resp.ModelVersion, NpgsqlDbType.Text)
                         .P("metrics", metrics, NpgsqlDbType.Jsonb)
                         .P("warnings", JsonSerializer.Serialize(warnings), NpgsqlDbType.Jsonb))
            await upd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task WritePoint(NpgsqlBinaryImporter copy, Guid runId, int? seriesId, DateOnly date,
        double yhat, double? lo, double? hi, CancellationToken ct)
    {
        await copy.StartRowAsync(ct);
        await copy.WriteAsync(runId, NpgsqlDbType.Uuid, ct);
        if (seriesId is { } s) await copy.WriteAsync(s, NpgsqlDbType.Integer, ct); else await copy.WriteNullAsync(ct);
        await copy.WriteAsync(date, NpgsqlDbType.Date, ct);
        await copy.WriteAsync(yhat, NpgsqlDbType.Double, ct);
        if (lo is { } l) await copy.WriteAsync(l, NpgsqlDbType.Double, ct); else await copy.WriteNullAsync(ct);
        if (hi is { } h) await copy.WriteAsync(h, NpgsqlDbType.Double, ct); else await copy.WriteNullAsync(ct);
    }
}
