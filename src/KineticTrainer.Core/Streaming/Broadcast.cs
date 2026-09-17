using System.Threading.Channels;

namespace KineticTrainer.Core.Streaming;

/// <summary>
/// Non-blocking one-to-many fan-out. Every subscriber gets its own bounded channel that drops
/// its oldest item when full, so a slow consumer (UI, TCP client, log file) can never block or
/// slow down the publisher (the BLE pipeline).
/// </summary>
public class Broadcast<T>
{
    private readonly object _gate = new();
    private Subscription[] _subscribers = [];

    /// <summary>Publishes an item to all current subscribers. Never blocks.</summary>
    public void Publish(T item)
    {
        var subs = Volatile.Read(ref _subscribers);
        foreach (var s in subs)
        {
            s.Offer(item);
        }
    }

    /// <summary>Subscribes with a bounded buffer. Dispose the subscription to unsubscribe.</summary>
    public Subscription Subscribe(string name, int capacity = 256)
    {
        var sub = new Subscription(this, name, capacity);
        lock (_gate)
        {
            _subscribers = [.. _subscribers, sub];
        }
        return sub;
    }

    public int SubscriberCount => Volatile.Read(ref _subscribers).Length;

    private void Remove(Subscription sub)
    {
        lock (_gate)
        {
            _subscribers = _subscribers.Where(s => !ReferenceEquals(s, sub)).ToArray();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly Broadcast<T> _owner;
        private readonly Channel<T> _channel;
        private long _dropped;

        internal Subscription(Broadcast<T> owner, string name, int capacity)
        {
            _owner = owner;
            Name = name;
            _channel = Channel.CreateBounded<T>(
                new BoundedChannelOptions(Math.Max(1, capacity))
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                _ => Interlocked.Increment(ref _dropped));
        }

        public string Name { get; }

        public ChannelReader<T> Reader => _channel.Reader;

        /// <summary>Items discarded because this subscriber fell behind.</summary>
        public long DroppedCount => Interlocked.Read(ref _dropped);

        internal void Offer(T item) => _channel.Writer.TryWrite(item);

        public void Dispose()
        {
            _owner.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}
