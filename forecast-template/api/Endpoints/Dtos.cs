using System.Text.Json.Serialization;
using Forecast.Api.Import;

namespace Forecast.Api.Endpoints;

// Публичный контракт (docs/contracts/public-api.yaml): camelCase, null-поля сериализуются всегда.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportDatasetRequest(string? Path, string? Name, Guid? DatasetId);

public sealed record ImportAccepted(Guid DatasetId, Guid JobId);

public sealed record JobDto(
    Guid JobId, string Kind, string Status, double? Progress, string? Message, Guid? DatasetId,
    long? Rows, int? SeriesCount, DateOnly? DateFrom, DateOnly? DateTo, IReadOnlyList<ImportWarning> Warnings,
    string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt);

public sealed record StoreDto(int StoreNbr, string? City, string? State, string? Type, int? Cluster);

public sealed record MetaDto(
    Guid? DatasetId, Guid? LatestDatasetId, IReadOnlyList<StoreDto> Stores, IReadOnlyList<string> Families,
    int SeriesCount, DateOnly? HistoryFrom, DateOnly? HistoryTo, int MaxHorizon, double IntervalLevel,
    Guid? LatestRunId, string? ModelVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateForecastRequest(Guid? DatasetId, int? Horizon, DateOnly? CutoffDate);

public sealed record ForecastAccepted(Guid RunId, string Status);

public sealed record MetricsDto(double? Rmsle, double? Mae, double? BaselineRmsle);

public sealed record ForecastRunDto(
    Guid RunId, Guid DatasetId, string Status, int Horizon, DateOnly CutoffDate, string? ModelVersion,
    double IntervalLevel, MetricsDto? Metrics, string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt);

public sealed record HistoryPoint(DateOnly Date, double Sales, long? Onpromotion);

public sealed record ForecastPoint(DateOnly Date, double Yhat, double? Lo, double? Hi);

public sealed record SeriesForecastDto(
    Guid? RunId, Guid DatasetId, string? ModelVersion, string Store, string Family, DateOnly? CutoffDate,
    double IntervalLevel, IReadOnlyList<HistoryPoint> History, IReadOnlyList<ForecastPoint> Forecast,
    MetricsDto? Metrics, DateTime? GeneratedAt);

public sealed record HealthChecksDto(string Db, string Ml);

public sealed record HealthDto(string Status, HealthChecksDto Checks, string? ModelVersion);

public static class Contract
{
    public const int MaxHorizon = 16;
    public const double IntervalLevel = 0.8;
}
