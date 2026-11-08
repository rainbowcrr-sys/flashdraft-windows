using System.Diagnostics;
using System.Threading.Channels;

namespace FlashDraft.Transport;

/// <summary>
/// Adjustable: throughput is the byte count observed over a two-second sliding
/// window. The ViewModel refreshes bound properties every UiRefreshMs.
/// </summary>
public sealed class SlidingThroughput
{
    private readonly object _gate = new();
    private readonly Queue<(long Timestamp, int Bytes)> _samples = new();
    private readonly long _windowTicks = TimeSpan.FromSeconds(Core.ProtocolConstants.ProgressWindowSeconds).Ticks;

    public double Record(int bytes)
    {
        long now = Stopwatch.GetTimestamp();
        long cutoff = now - _windowTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        lock (_gate)
        {
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 0 && _samples.Peek().Timestamp < cutoff)
            {
                _samples.Dequeue();
            }

            long total = 0;
            foreach ((_, int sample) in _samples)
            {
                total += sample;
            }

            return total / (double)Core.ProtocolConstants.ProgressWindowSeconds;
        }
    }
}

/// <summary>
/// IProgress implementation that pushes immutable snapshots into a bounded
/// Channel. The ViewModel reads the channel and marshals updates to the UI thread.
/// </summary>
public sealed class ChannelProgressReporter<T> : IProgress<T>
{
    private readonly Channel<T> _channel = Channel.CreateBounded<T>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ChannelReader<T> Reader => _channel.Reader;

    public void Report(T value) => _channel.Writer.TryWrite(value);

    public void Complete() => _channel.Writer.TryComplete();
}
