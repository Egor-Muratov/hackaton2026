namespace Forecast.Api.Infrastructure;

/// <summary>Короткие фабрики ProblemDetails (application/problem+json; traceId добавляет CustomizeProblemDetails).</summary>
public static class Problems
{
    public static IResult NotFound(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not Found");

    public static IResult Conflict(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    public static IResult Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
