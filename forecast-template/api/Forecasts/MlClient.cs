using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forecast.Api.Infrastructure;

namespace Forecast.Api.Forecasts;

/// <summary>
/// Клиент ML-сервиса (docs/contracts/ml-api.yaml). Весь JSON к ML — snake_case; наружу (фронту) — camelCase.
/// Таймауты и ретраи /predict настраиваются в Program.cs (Microsoft.Extensions.Http.Resilience) по Ml__*.
/// </summary>
public sealed class MlClient(IHttpClientFactory factory)
{
    public const string PredictClient = "ml-predict";
    public const string HealthClient = "ml-health";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public async Task<MlHealth> HealthAsync(string? correlationId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "health");
        if (correlationId is not null) req.Headers.TryAddWithoutValidation(Correlation.Header, correlationId);
        using var resp = await factory.CreateClient(HealthClient).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<MlHealth>(Json, ct)
               ?? throw new InvalidDataException("ML /health: пустой ответ");
    }

    public async Task<PredictResponse> PredictAsync(PredictRequest body, string? correlationId, CancellationToken ct)
    {
        // Тело буферизуем: Content-Length вместо chunked, и его можно безопасно переотправить при ретрае.
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "predict") { Content = content };
        if (correlationId is not null) req.Headers.TryAddWithoutValidation(Correlation.Header, correlationId);
        using var resp = await factory.CreateClient(PredictClient).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new MlException((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        return await resp.Content.ReadFromJsonAsync<PredictResponse>(Json, ct)
               ?? throw new InvalidDataException("ML /predict: пустой ответ");
    }
}

public sealed class MlException(int status, string body)
    : Exception($"ML /predict -> HTTP {status}: {(body.Length > 2000 ? body[..2000] + "…" : body)}")
{
    public int Status { get; } = status;
}

// ---- DTO ML (snake_case через MlClient.Json) ----

public sealed record MlHealth(string? Status, string? ModelVersion, bool ModelLoaded);

public sealed record PredictRequest(
    Guid DatasetId,
    Guid? RunId,
    DateOnly CutoffDate,
    int Horizon,
    List<Dictionary<string, JsonElement>>? SeriesKeys,
    bool IncludeAggregate,
    double Interval);

public sealed record PredictResponse(
    string? ModelVersion,
    Guid DatasetId,
    DateOnly CutoffDate,
    int Horizon,
    double Interval,
    List<MlForecastPoint> Forecasts,
    List<MlAggregatePoint>? Aggregate,
    Dictionary<string, double?>? CvMetrics,
    List<string>? Warnings);

public sealed record MlForecastPoint(Dictionary<string, JsonElement> SeriesKey, DateOnly Date, double Yhat, double? Lower, double? Upper);

public sealed record MlAggregatePoint(DateOnly Date, double Yhat, double? Lower, double? Upper);
