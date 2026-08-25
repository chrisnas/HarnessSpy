using System.Threading.Channels;

namespace HarnessSpy.Core.Services;

public sealed class ObservationBuffer<T>
{
    private readonly Channel<T> _channel;

    public ObservationBuffer(int capacity = 2048)
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ValueTask WriteAsync(T observation, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(observation, cancellationToken);

    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public bool TryRead(out T? observation) =>
        _channel.Reader.TryRead(out observation);

    public void Complete(Exception? exception = null) =>
        _channel.Writer.TryComplete(exception);
}
