using Forecast.Api.Forecasts;
using Forecast.Api.Infrastructure;
using Npgsql;

namespace Forecast.Api.Endpoints;

/// <summary>/health/live — процесс жив; /health/ready и /api/health — БД обязательна (иначе 503), ML даёт Degraded.</summary>
public static class HealthEndpoints
{
    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => TypedResults.Ok(new { status = "Healthy" })).WithTags("health");
        app.MapGet("/health/ready", Ready).WithTags("health");
        app.MapGet("/api/health", Ready).WithTags("health").WithName("getHealth");
    }

    private static async Task<IResult> Ready(HttpContext http, NpgsqlDataSource ds, MlClient ml, ILoggerFactory lf,
        CancellationToken ct)
    {
        var log = lf.CreateLogger("Health");
        var dbTask = CheckDbAsync(ds, log, ct);
        var mlTask = CheckMlAsync(ml, Correlation.Get(http), log, ct);
        var db = await dbTask;
        var (mlStatus, modelVersion) = await mlTask;

        var status = db != "Healthy" ? "Unhealthy" : mlStatus != "Healthy" ? "Degraded" : "Healthy";
        var body = new HealthDto(status, new HealthChecksDto(db, mlStatus), modelVersion);
        return TypedResults.Json(body, statusCode: status == "Unhealthy" ? 503 : 200);
    }

    private static async Task<string> CheckDbAsync(NpgsqlDataSource ds, ILogger log, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await using var cmd = ds.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cts.Token);
            return "Healthy";
        }
        catch (Exception ex)
        {
            log.LogWarning("DB health: {Error}", ex.Message);
            return "Unhealthy";
        }
    }

    private static async Task<(string, string?)> CheckMlAsync(MlClient ml, string? corr, ILogger log, CancellationToken ct)
    {
        try
        {
            var h = await ml.HealthAsync(corr, ct);
            return (h.ModelLoaded ? "Healthy" : "Degraded", h.ModelVersion);
        }
        catch (Exception ex)
        {
            log.LogWarning("ML health: {Error}", ex.Message);
            return ("Unhealthy", null);
        }
    }
}
