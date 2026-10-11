using System.Net;
using System.Text.Json;
using Forecast.Api.Endpoints;
using Forecast.Api.Forecasts;
using Forecast.Api.Import;
using Forecast.Api.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Http.Resilience;
using Npgsql;
using Polly;
using Polly.Timeout;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ---- Логи: Serilog в stdout (JSON вне Development) -----------------------------------------------
builder.Services.AddSerilog((_, lc) =>
{
    lc.MinimumLevel.Information()
      .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
      .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
      .MinimumLevel.Override("Polly", LogEventLevel.Warning)
      .Enrich.FromLogContext()
      .ReadFrom.Configuration(builder.Configuration);
    if (builder.Environment.IsDevelopment())
        lc.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {CorrelationId}{NewLine}{Exception}");
    else
        lc.WriteTo.Console(new RenderedCompactJsonFormatter());
});

// ---- Настройки (env: ConnectionStrings__Forecast, Ml__*, Import__Root) -------------------------
builder.Services.Configure<MlOptions>(builder.Configuration.GetSection("Ml"));
builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection("Import"));
var ml = builder.Configuration.GetSection("Ml").Get<MlOptions>() ?? new MlOptions();

var connString = builder.Configuration.GetConnectionString("Forecast");
if (string.IsNullOrWhiteSpace(connString))
    throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__Forecast");
// TODO(EF Core): AddDbContext<ForecastDb>(o => o.UseNpgsql(dataSource)) поверх того же NpgsqlDataSource.
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connString) { Name = "forecast" }.Build());

// ---- ML-клиент: /predict — 120 c на попытку, 300 c всего, 2 ретрая только на 5xx/сеть; /health — 2 c без ретраев
var mlBase = new Uri(ml.BaseUrl.TrimEnd('/') + "/");
builder.Services.AddSingleton<MlClient>();
builder.Services.AddHttpClient(MlClient.HealthClient, c =>
{
    c.BaseAddress = mlBase;
    c.Timeout = TimeSpan.FromSeconds(ml.HealthTimeoutSeconds);
});
builder.Services.AddHttpClient(MlClient.PredictClient, c =>
    {
        c.BaseAddress = mlBase;
        c.Timeout = Timeout.InfiniteTimeSpan; // таймауты задаёт resilience-пайплайн
    })
    .AddResilienceHandler("ml-predict", p =>
    {
        p.AddTimeout(TimeSpan.FromSeconds(ml.PredictTotalTimeoutSeconds));
        p.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = Math.Max(0, ml.PredictRetries),
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2),
            UseJitter = true,
            // 422 и прочие 4xx не ретраим (CONVENTIONS): только 5xx (включая 503 «модель не загружена») и сеть.
            ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
            {
                { Exception: HttpRequestException or TimeoutRejectedException } => true,
                { Result: { } r } => (int)r.StatusCode >= 500,
                _ => false,
            }),
        });
        p.AddTimeout(TimeSpan.FromSeconds(ml.PredictAttemptTimeoutSeconds));
    });

// ---- Фоновые задачи (очередь в памяти, статус в БД) ---------------------------------------------
builder.Services.AddSingleton<WorkQueue<ImportWork>>();
builder.Services.AddSingleton<WorkQueue<ForecastWork>>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHostedService<ForecastWorker>();

// ---- HTTP: camelCase JSON, ProblemDetails с traceId = X-Correlation-Id, OpenAPI ------------------
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Extensions["traceId"] = Correlation.Get(ctx.HttpContext) ?? ctx.HttpContext.TraceIdentifier;
    ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
});
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true); // ошибки биндинга -> 400 ProblemDetails
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseCorrelationId();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/api/health")
        ? LogEventLevel.Verbose : LogEventLevel.Information);
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var problems = ctx.RequestServices.GetRequiredService<IProblemDetailsService>();
    if (ex is BadHttpRequestException bad)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        var pd = new HttpValidationProblemDetails(new Dictionary<string, string[]>
        {
            ["body"] = [bad.InnerException?.Message ?? bad.Message],
        }) { Status = 400, Title = "Bad Request" };
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = ctx, ProblemDetails = pd });
        return;
    }
    ctx.Response.StatusCode = ex is NpgsqlException ? (int)HttpStatusCode.ServiceUnavailable : 500;
    await problems.WriteAsync(new ProblemDetailsContext
    {
        HttpContext = ctx,
        ProblemDetails = { Status = ctx.Response.StatusCode, Title = ex is NpgsqlException ? "Database unavailable" : "Internal Server Error" },
    });
}));
app.UseStatusCodePages();

app.MapOpenApi();                 // /openapi/v1.json
app.MapScalarApiReference();      // /scalar
app.MapHealth();
app.MapDatasets();
app.MapForecasts();

app.Run();
