using System.Threading.Channels;

namespace Forecast.Api.Infrastructure;

/// <summary>Очередь в памяти для фоновых задач. Статус живёт в БД; после рестарта незавершённые задачи помечаются failed.</summary>
public sealed class WorkQueue<T>
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
    public ValueTask EnqueueAsync(T item, CancellationToken ct = default) => _channel.Writer.WriteAsync(item, ct);
    public ChannelReader<T> Reader => _channel.Reader;
}
