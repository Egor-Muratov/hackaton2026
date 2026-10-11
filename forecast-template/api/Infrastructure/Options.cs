namespace Forecast.Api.Infrastructure;

/// <summary>Секция Ml (env Ml__*), см. docs/contracts/CONVENTIONS.md.</summary>
public sealed class MlOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public int PredictAttemptTimeoutSeconds { get; set; } = 120;
    public int PredictTotalTimeoutSeconds { get; set; } = 300;
    public int PredictRetries { get; set; } = 2;
    public int HealthTimeoutSeconds { get; set; } = 2;
}

/// <summary>Секция Import (env Import__Root): корень для относительных путей импорта.</summary>
public sealed class ImportOptions
{
    public string Root { get; set; } = "/data";
}
