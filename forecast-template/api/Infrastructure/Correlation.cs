using Serilog.Context;

namespace Forecast.Api.Infrastructure;

/// <summary>X-Correlation-Id: берём из запроса или генерируем, возвращаем в ответе, кладём в логи и ProblemDetails.traceId.</summary>
public static class Correlation
{
    public const string Header = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    public static string? Get(HttpContext ctx) => ctx.Items[ItemKey] as string;

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var id = ctx.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) id = Guid.NewGuid().ToString("N");
        ctx.Items[ItemKey] = id;
        ctx.Response.Headers[Header] = id;
        using (LogContext.PushProperty("CorrelationId", id))
            await next(ctx);
    });
}
