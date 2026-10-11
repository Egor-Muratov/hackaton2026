using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Forecast.Api.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using Serilog.Context;

namespace Forecast.Api.Import;

/// <summary>
/// Фоновый импорт CSV: потоковое чтение файла, ряды (store_nbr, family) резолвятся в series.id через словарь в памяти,
/// строки идут в observations через COPY BINARY одной транзакцией.
/// Быстрый путь (в датасете ещё нет наблюдений): COPY прямо в observations.
/// Дозапись (test.csv в существующий датасет) и дубли (ряд, дата): COPY во временную таблицу + upsert.
/// Импорты выполняются строго по очереди (один читатель канала), поэтому test.csv можно ставить сразу за train.csv.
/// </summary>
public sealed class ImportWorker(
    WorkQueue<ImportWork> queue,
    NpgsqlDataSource ds,
    ILogger<ImportWorker> log) : BackgroundService
{
    private const int MaxWarnings = 100;
    private const long ProgressEveryRows = 50_000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);
    private const long DropFkMinFileBytes = 10L << 20;
    private static readonly string[] Required = ["date", "store_nbr", "family", "onpromotion"];

    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedAsync(stoppingToken);
        await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using (LogContext.PushProperty("CorrelationId", work.CorrelationId))
            using (LogContext.PushProperty("JobId", work.JobId))
            {
                // Импорт синхронный (COPY с синхронными Write быстрее на миллионах значений) — уводим с потока хоста.
                await Task.Run(() => Run(work, stoppingToken), CancellationToken.None);
            }
        }
    }

    /// <summary>Задачи, прерванные рестартом API, переводим в failed (очередь в памяти не переживает рестарт).</summary>
    private async Task FailInterruptedAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.Cmd("""
                WITH j AS (
                  UPDATE jobs SET status = 'failed', error = 'Прервано рестартом API', finished_at = now()
                  WHERE status IN ('queued', 'running') AND kind = 'import'
                  RETURNING dataset_id)
                UPDATE datasets SET status = 'failed'
                WHERE status = 'importing' AND id IN (SELECT dataset_id FROM j);
                """);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Не удалось пометить прерванные импорты (БД недоступна?)");
        }
    }

    private void Run(ImportWork w, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new ImportContext(w, ds);
        try
        {
            SetJob(w.JobId, "UPDATE jobs SET status = 'running', started_at = now(), progress = 0, message = 'Чтение CSV' WHERE id = @id");
            log.LogInformation("Импорт {Path} в датасет {DatasetId} начат", w.FullPath, w.DatasetId);

            bool append = !w.NewDataset && DatasetHasObservations(w.DatasetId);
            ImportCounts counts;
            if (append)
            {
                counts = ImportStaged(ctx, ct);
            }
            else
            {
                try { counts = ImportDirect(ctx, ct); }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    log.LogWarning("В файле есть дубли (ряд, дата) — повторяем через staging-таблицу с upsert");
                    ctx = new ImportContext(w, ds);
                    counts = ImportStaged(ctx, ct);
                }
            }
            var copySeconds = sw.Elapsed.TotalSeconds;

            SetProgress(w.JobId, 0.95, "Сводка по датасету");
            ApplyStoreAttrs(w);
            var summary = Finalize(w, counts.Rows >= 100_000);

            var result = JsonSerializer.Serialize(new
            {
                rows = counts.Rows,
                seriesCount = summary.SeriesCount,
                dateFrom = summary.DateFrom,
                dateTo = summary.DateTo,
                datasetRows = summary.Rows,
                skipped = ctx.Skipped,
                seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            });
            var msg = $"Готово: {counts.Rows:N0} строк за {sw.Elapsed.TotalSeconds:0.0} с (COPY {copySeconds:0.0} с), пропущено {ctx.Skipped}";
            using var conn = ds.OpenConnection();
            using var cmd = conn.Cmd("""
                UPDATE jobs SET status = 'done', progress = 1, message = @msg, result = @result, warnings = @warnings,
                                finished_at = now()
                WHERE id = @id
                """)
                .P("id", w.JobId, NpgsqlDbType.Uuid)
                .P("msg", msg, NpgsqlDbType.Text)
                .P("result", result, NpgsqlDbType.Jsonb)
                .P("warnings", JsonSerializer.Serialize(ctx.Warnings, JsonSerializerOptions.Web), NpgsqlDbType.Jsonb);
            cmd.ExecuteNonQuery();
            log.LogInformation("Импорт {Path}: {Rows} строк, {Series} рядов, {Seconds:0.0} с (COPY {Copy:0.0} с), пропущено {Skipped}",
                w.FullPath, counts.Rows, summary.SeriesCount, sw.Elapsed.TotalSeconds, copySeconds, ctx.Skipped);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Импорт {Path} упал", w.FullPath);
            try
            {
                using var conn = ds.OpenConnection();
                using var cmd = conn.Cmd("""
                    UPDATE jobs SET status = 'failed', error = @err, warnings = @warnings, finished_at = now() WHERE id = @id;
                    UPDATE datasets SET status = 'failed' WHERE id = @ds AND @isNew AND status = 'importing';
                    """)
                    .P("id", w.JobId, NpgsqlDbType.Uuid)
                    .P("ds", w.DatasetId, NpgsqlDbType.Uuid)
                    .P("isNew", w.NewDataset, NpgsqlDbType.Boolean)
                    .P("err", ex is OperationCanceledException ? "Остановлено при выключении API" : ex.Message, NpgsqlDbType.Text)
                    .P("warnings", JsonSerializer.Serialize(ctx.Warnings, JsonSerializerOptions.Web), NpgsqlDbType.Jsonb);
                cmd.ExecuteNonQuery();
            }
            catch (Exception inner)
            {
                log.LogError(inner, "Не удалось записать статус failed для задачи {JobId}", w.JobId);
            }
        }
    }

    // ---------------------------------------------------------------- быстрый путь

    private ImportCounts ImportDirect(ImportContext ctx, CancellationToken ct)
    {
        using var conn = ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Проход 1 (~несколько секунд): собираем ключи рядов и вставляем их одним запросом.
        // Во время COPY соединение занято, а снятый FK блокирует series — поэтому все ряды должны быть известны заранее.
        var keys = new HashSet<(int Store, string Family)>();
        ParseFile(new ImportContext(ctx.Work, ds), ct, (in CsvRow row) => keys.Add((row.Store, row.Family)),
            "Сбор рядов", 0, 0.1);
        using (var ins = conn.Cmd("""
                   INSERT INTO series (dataset_id, store_nbr, family)
                   SELECT @ds, s, f FROM unnest(@stores, @families) AS t(s, f)
                   ON CONFLICT ON CONSTRAINT series_key_uq DO NOTHING
                   """, tx)
                   .P("ds", ctx.Work.DatasetId, NpgsqlDbType.Uuid)
                   .P("stores", keys.Select(k => k.Store).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer)
                   .P("families", keys.Select(k => k.Family).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text))
            ins.ExecuteNonQuery();
        var series = LoadSeries(conn, tx, ctx.Work.DatasetId);

        // Замер на 3 млн строк (PG 13): COPY с FK observations -> series ~105 с, из них ~85 с — построчные RI-проверки.
        // Для больших файлов снимаем FK внутри транзакции и возвращаем его тем же определением (проверка целиком <1 с).
        // Цена: на время COPY observations/series заблокированы (ACCESS EXCLUSIVE) — чтения ML и API ждут.
        var droppedFks = new List<(string Name, string Def)>();
        if (new FileInfo(ctx.Work.FullPath).Length >= DropFkMinFileBytes)
        {
            using (var q = conn.Cmd("""
                       SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint
                       WHERE conrelid = 'observations'::regclass AND contype = 'f'
                       """, tx))
            using (var r = q.ExecuteReader())
                while (r.Read()) droppedFks.Add((r.GetString(0), r.GetString(1)));
            foreach (var fk in droppedFks)
            {
                using var drop = conn.Cmd($"ALTER TABLE observations DROP CONSTRAINT {QuoteIdent(fk.Name)}", tx);
                drop.ExecuteNonQuery();
            }
        }

        // Проход 2: COPY BINARY прямо в observations.
        long rows;
        using (var copy = conn.BeginBinaryImport(
                   "COPY observations (series_id, date, sales, onpromotion) FROM STDIN (FORMAT BINARY)"))
        {
            copy.Timeout = TimeSpan.FromMinutes(30);
            ParseFile(ctx, ct, (in CsvRow row) =>
            {
                var seriesId = series[(row.Store, row.Family)];
                copy.StartRow();
                copy.Write(seriesId, NpgsqlDbType.Integer);
                copy.Write(row.Date, NpgsqlDbType.Date);
                if (row.Sales is { } s) copy.Write(s, NpgsqlDbType.Double); else copy.WriteNull();
                if (row.Promo is { } p) copy.Write(p, NpgsqlDbType.Integer); else copy.WriteNull();
            }, "COPY", 0.1, 0.9);
            SetProgress(ctx.Work.JobId, 0.9, $"COPY {ctx.Rows / 1e6:0.00}M строк: фиксация");
            rows = (long)copy.Complete();
        }
        foreach (var fk in droppedFks)
        {
            using var add = conn.Cmd($"ALTER TABLE observations ADD CONSTRAINT {QuoteIdent(fk.Name)} {fk.Def}", tx);
            add.CommandTimeout = 600;
            add.ExecuteNonQuery();
        }
        tx.Commit();
        return new ImportCounts(rows);
    }

    // ---------------------------------------------------------------- дозапись / upsert

    private ImportCounts ImportStaged(ImportContext ctx, CancellationToken ct)
    {
        using var conn = ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var create = conn.Cmd("""
                   CREATE TEMP TABLE import_stage (line bigint, store_nbr integer, family text, date date,
                                                   sales double precision, onpromotion integer) ON COMMIT DROP
                   """, tx))
            create.ExecuteNonQuery();

        using (var copy = conn.BeginBinaryImport(
                   "COPY import_stage (line, store_nbr, family, date, sales, onpromotion) FROM STDIN (FORMAT BINARY)"))
        {
            copy.Timeout = TimeSpan.FromMinutes(30);
            ParseFile(ctx, ct, (in CsvRow row) =>
            {
                copy.StartRow();
                copy.Write(row.Line, NpgsqlDbType.Bigint);
                copy.Write(row.Store, NpgsqlDbType.Integer);
                copy.Write(row.Family, NpgsqlDbType.Text);
                copy.Write(row.Date, NpgsqlDbType.Date);
                if (row.Sales is { } s) copy.Write(s, NpgsqlDbType.Double); else copy.WriteNull();
                if (row.Promo is { } p) copy.Write(p, NpgsqlDbType.Integer); else copy.WriteNull();
            });
            copy.Complete();
        }

        SetProgress(ctx.Work.JobId, 0.9, $"Upsert {ctx.Rows / 1e6:0.00}M строк");
        using var merge = conn.Cmd("""
            INSERT INTO series (dataset_id, store_nbr, family)
            SELECT DISTINCT @ds, store_nbr, family FROM import_stage
            ON CONFLICT ON CONSTRAINT series_key_uq DO NOTHING;

            -- Совпадающие (ряд, дата) перезаписываются; при дублях внутри файла побеждает последняя строка.
            INSERT INTO observations (series_id, date, sales, onpromotion)
            SELECT DISTINCT ON (s.id, st.date) s.id, st.date, st.sales, st.onpromotion
            FROM import_stage st
            JOIN series s ON s.dataset_id = @ds AND s.store_nbr = st.store_nbr AND s.family = st.family
            ORDER BY s.id, st.date, st.line DESC
            ON CONFLICT ON CONSTRAINT observations_pk
            DO UPDATE SET sales = EXCLUDED.sales, onpromotion = EXCLUDED.onpromotion;
            """, tx).P("ds", ctx.Work.DatasetId, NpgsqlDbType.Uuid);
        merge.CommandTimeout = 1800;
        merge.ExecuteNonQuery();
        tx.Commit();
        return new ImportCounts(ctx.Rows);
    }

    // ---------------------------------------------------------------- разбор CSV

    private delegate void RowSink(in CsvRow row);

    private readonly record struct CsvRow(long Line, int Store, string Family, DateOnly Date, double? Sales, int? Promo);

    private static void ParseFile(ImportContext ctx, CancellationToken ct, RowSink sink,
        string phase = "COPY", double progressFrom = 0, double progressTo = 0.9)
    {
        using var file = new FileStream(ctx.Work.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        long length = Math.Max(1, file.Length);

        var header = reader.ReadLine() ?? throw new InvalidDataException("Пустой файл");
        var fields = new Range[64];
        int n = Csv.Split(header, fields);
        if (n < 0 || n > fields.Length) throw new InvalidDataException("Не удалось разобрать заголовок CSV");
        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++) cols.TryAdd(Csv.Field(header, fields[i]).ToString(), i);
        var missing = Required.Where(c => !cols.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Нет обязательных колонок: {string.Join(", ", missing)} (ожидаются date, store_nbr, family, onpromotion[, sales])");
        int cDate = cols["date"], cStore = cols["store_nbr"], cFamily = cols["family"], cPromo = cols["onpromotion"];
        int cSales = cols.GetValueOrDefault("sales", -1);

        // Интернирование семейств без аллокации строки на каждую запись.
        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        var familyLookup = families.GetAlternateLookup<ReadOnlySpan<char>>();

        long lineNo = 1;
        var nextProgress = Stopwatch.StartNew();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0) continue;
            if ((lineNo & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int count = Csv.Split(line, fields);
            if (count != n) { ctx.Warn(lineNo, count < 0 ? "Незакрытая кавычка" : $"Ожидалось {n} полей, получено {count}"); continue; }

            if (!DateOnly.TryParseExact(Csv.Field(line, fields[cDate]), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            { ctx.Warn(lineNo, "date: ожидается YYYY-MM-DD"); continue; }

            if (!int.TryParse(Csv.Field(line, fields[cStore]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var store))
            { ctx.Warn(lineNo, "store_nbr: ожидается целое"); continue; }

            var famSpan = Csv.Field(line, fields[cFamily]);
            if (famSpan.IsEmpty) { ctx.Warn(lineNo, "family: пустое значение"); continue; }
            if (!familyLookup.TryGetValue(famSpan, out var family))
            {
                family = famSpan.ToString();
                families[family] = family;
            }

            double? sales = null;
            if (cSales >= 0)
            {
                var s = Csv.Field(line, fields[cSales]);
                if (!s.IsEmpty)
                {
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0)
                    { ctx.Warn(lineNo, "sales: ожидается число >= 0"); continue; }
                    sales = v;
                }
            }

            int? promo = null;
            var ps = Csv.Field(line, fields[cPromo]);
            if (!ps.IsEmpty)
            {
                if (!int.TryParse(ps, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p < 0)
                { ctx.Warn(lineNo, "onpromotion: ожидается целое >= 0"); continue; }
                promo = p;
            }

            sink(new CsvRow(lineNo, store, family, date, sales, promo));
            ctx.Rows++;

            if (ctx.Rows % ProgressEveryRows == 0 && nextProgress.Elapsed >= ProgressInterval)
            {
                nextProgress.Restart();
                ctx.Progress(progressFrom + (progressTo - progressFrom) * file.Position / length, $"{phase} {ctx.Rows / 1e6:0.00}M строк");
            }
        }
    }

    // ---------------------------------------------------------------- вспомогательное

    private static Dictionary<(int, string), int> LoadSeries(NpgsqlConnection conn, NpgsqlTransaction tx, Guid datasetId)
    {
        var map = new Dictionary<(int, string), int>();
        using var cmd = conn.Cmd("SELECT id, store_nbr, family FROM series WHERE dataset_id = @ds", tx)
            .P("ds", datasetId, NpgsqlDbType.Uuid);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[(r.GetInt32(1), r.GetString(2))] = r.GetInt32(0);
        return map;
    }

    private bool DatasetHasObservations(Guid datasetId)
    {
        using var conn = ds.OpenConnection();
        using var cmd = conn.Cmd("""
            SELECT EXISTS (SELECT 1 FROM observations o JOIN series s ON s.id = o.series_id WHERE s.dataset_id = @ds)
            """).P("ds", datasetId, NpgsqlDbType.Uuid);
        return (bool)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Репетиционная эвристика: если рядом с файлом лежит stores.csv (store_nbr, city, state, type, cluster),
    /// кладём атрибуты магазина в series.attrs — их отдаёт /api/meta. Нет файла — атрибуты null.
    /// </summary>
    private void ApplyStoreAttrs(ImportWork w)
    {
        var path = Path.Combine(Path.GetDirectoryName(w.FullPath)!, "stores.csv");
        if (!File.Exists(path)) return;
        try
        {
            using var reader = new StreamReader(path);
            var header = reader.ReadLine();
            if (header is null) return;
            var fields = new Range[16];
            int n = Csv.Split(header, fields);
            var cols = Enumerable.Range(0, Math.Max(0, n)).ToDictionary(i => Csv.Field(header, fields[i]).ToString(), i => i,
                StringComparer.OrdinalIgnoreCase);
            if (!cols.TryGetValue("store_nbr", out var cStore)) return;

            using var conn = ds.OpenConnection();
            using var batch = new NpgsqlBatch(conn);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (Csv.Split(line, fields) != n) continue;
                if (!int.TryParse(Csv.Field(line, fields[cStore]), out var store)) continue;
                string? Get(string name) => cols.TryGetValue(name, out var i) ? Csv.Field(line, fields[i]).ToString() : null;
                int? cluster = int.TryParse(Get("cluster"), out var cl) ? cl : null;
                var attrs = JsonSerializer.Serialize(new { city = Get("city"), state = Get("state"), type = Get("type"), cluster });
                var cmd = new NpgsqlBatchCommand(
                    "UPDATE series SET attrs = attrs || @attrs WHERE dataset_id = @ds AND store_nbr = @store");
                cmd.Parameters.Add(new NpgsqlParameter("attrs", NpgsqlDbType.Jsonb) { Value = attrs });
                cmd.Parameters.Add(new NpgsqlParameter("ds", NpgsqlDbType.Uuid) { Value = w.DatasetId });
                cmd.Parameters.Add(new NpgsqlParameter("store", NpgsqlDbType.Integer) { Value = store });
                batch.BatchCommands.Add(cmd);
            }
            if (batch.BatchCommands.Count > 0) batch.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "stores.csv не применён");
        }
    }

    private sealed record Summary(long Rows, int SeriesCount, DateOnly? DateFrom, DateOnly? DateTo);

    private Summary Finalize(ImportWork w, bool analyze)
    {
        using var conn = ds.OpenConnection();
        if (analyze)
        {
            using var an = conn.Cmd("ANALYZE observations; ANALYZE series;");
            an.CommandTimeout = 600;
            an.ExecuteNonQuery();
        }
        using var cmd = conn.Cmd("""
            WITH s AS (
              SELECT count(*) AS rows,
                     min(o.date) FILTER (WHERE o.sales IS NOT NULL) AS date_from,
                     max(o.date) FILTER (WHERE o.sales IS NOT NULL) AS date_to
              FROM observations o JOIN series s ON s.id = o.series_id
              WHERE s.dataset_id = @ds)
            UPDATE datasets d
            SET status = 'ready', ready_at = now(), rows = s.rows,
                series_count = (SELECT count(*) FROM series WHERE dataset_id = @ds),
                date_from = s.date_from, date_to = s.date_to
            FROM s
            WHERE d.id = @ds
            RETURNING d.rows, d.series_count, d.date_from, d.date_to
            """).P("ds", w.DatasetId, NpgsqlDbType.Uuid);
        cmd.CommandTimeout = 600;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"Датасет {w.DatasetId} удалён во время импорта");
        return new Summary(r.GetInt64(0), r.GetInt32(1), r.Val<DateOnly>(2), r.Val<DateOnly>(3));
    }

    private void SetJob(Guid jobId, string sql)
    {
        using var conn = ds.OpenConnection();
        using var cmd = conn.Cmd(sql).P("id", jobId, NpgsqlDbType.Uuid);
        cmd.ExecuteNonQuery();
    }

    private void SetProgress(Guid jobId, double progress, string message) => ImportContext.WriteProgress(ds, jobId, progress, message);

    private sealed record ImportCounts(long Rows);

    private sealed class ImportContext(ImportWork work, NpgsqlDataSource ds)
    {
        public ImportWork Work { get; } = work;
        public long Rows { get; set; }
        public long Skipped { get; private set; }
        public List<ImportWarning> Warnings { get; } = [];

        public void Warn(long line, string message)
        {
            Skipped++;
            if (Warnings.Count < MaxWarnings) Warnings.Add(new ImportWarning(line, message));
        }

        public void Progress(double progress, string message) => WriteProgress(ds, Work.JobId, progress, message);

        public static void WriteProgress(NpgsqlDataSource ds, Guid jobId, double progress, string message)
        {
            using var conn = ds.OpenConnection();
            using var cmd = conn.Cmd("UPDATE jobs SET progress = @p, message = @m WHERE id = @id")
                .P("id", jobId, NpgsqlDbType.Uuid)
                .P("p", (float)Math.Clamp(progress, 0, 1), NpgsqlDbType.Real)
                .P("m", message, NpgsqlDbType.Text);
            cmd.ExecuteNonQuery();
        }
    }
}
