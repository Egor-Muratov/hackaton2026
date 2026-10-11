namespace Forecast.Api.Import;

public sealed record ImportWork(Guid JobId, Guid DatasetId, string FullPath, bool NewDataset, string? CorrelationId);

public sealed record ImportWarning(long? Line, string Message);
