using System.Collections.Concurrent;
using System.Threading.Channels;
using EdgeForge.Core;

namespace EdgeForge.Runtime;

/// <summary>
/// Fans one capture loop out to many viewers: takes frames from the capture thread,
/// encodes each one exactly once on a thread of its own, and hands the result to
/// every subscriber that is ready for it.
/// </summary>
/// <remarks>
/// <para><b>Why a dedicated encoder thread.</b> Encoding a 2560x1920 frame to JPEG
/// costs 15-30 ms. Doing it on the capture thread would add that to a loop whose
/// inference budget is ~41 ms, roughly halving detector throughput - trading the
/// product for a picture. Doing it per subscriber would repeat identical work once
/// per viewer. So it happens once, here, off the capture path.</para>
///
/// <para><b>Frame lifetime - the sharp edge.</b> A Mat must never cross into an
/// async HTTP handler. <see cref="Offer"/> runs inside the session's snapshot gate
/// with the frame still alive; it clones and returns, and the clone belongs to the
/// encoder thread, which frees it in a finally. Only the immutable byte[] in
/// <see cref="EncodedFrame"/> reaches a consumer. Holding a Mat across an await
/// instead is a use-after-free that surfaces as a rare native crash under a slow
/// client, with no managed stack to read.</para>
///
/// <para><b>Slow consumers drop, never block.</b> The pending slot holds exactly one
/// frame, so memory is bounded at two clones however far behind the encoder falls.
/// A viewer parked in a socket write simply does not observe the frames that pass
/// meanwhile; when it returns it gets the newest. Nothing queues per subscriber and
/// nothing back-pressures the capture thread.</para>
///
/// <para><b>Idle when unwatched.</b> With no subscribers, <see cref="Offer"/> returns
/// on a volatile read before touching the frame, and the encoder blocks forever on
/// its semaphore. The marginal cost of this class with nobody watching is that one
/// read per frame.</para>
///
/// <para><b>Currently uncalled - kept deliberately.</b> Nothing constructs this any
/// more. It existed to serve MJPEG to browsers from <c>EdgeForge.Web</c>; that job
/// now belongs to MediaMTX, which pulls the camera's RTSP stream directly and
/// republishes it over WebRTC, so no frame leaves the .NET process for display.
/// The class stays because the problem it solves - encode once off the capture
/// path, fan out without back-pressuring capture - is the one that reappears the
/// moment .NET itself needs to record a clip. It remains compiled and covered by
/// <c>FrameBroadcasterTests</c>, which is what keeps preserved code from rotting.
/// The same goes for its collaborators: <see cref="EncodedFrame"/> and the
/// <c>BitmapBridge</c> Mat helpers it binds to.</para>
/// </remarks>
public sealed class FrameBroadcaster : IAsyncDisposable
{
    /// <summary>Clones a frame's pixels into a handle the encoder owns outright.</summary>
    /// <remarks>
    /// Injected so the broadcaster can be tested without OpenCV, and so the only
    /// code that knows what a Mat is stays in EdgeForge.Vision.
    /// </remarks>
    public delegate object? FrameCloner(Frame frame);

    /// <summary>Encodes a clone. Does not free it; the disposer does.</summary>
    public delegate byte[]? FrameEncoder(object clone, IReadOnlyList<Detection> detections);

    /// <summary>Frees a clone's native memory.</summary>
    /// <remarks>
    /// Separate from the encoder because a dropped frame must be freed without being
    /// encoded. Both run on the encoder thread, never on the capture thread.
    /// </remarks>
    public delegate void FrameDisposer(object clone);

    private readonly FrameCloner _clone;
    private readonly FrameEncoder _encode;
    private readonly FrameDisposer _dispose;
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _time;

    // A one-slot signal channel rather than a SemaphoreSlim. The semaphore version
    // has a lost-wakeup race: the encoder must check the slot before waiting, and a
    // frame offered in the window between that check and the wait leaves its permit
    // to be consumed by an iteration that finds nothing - after which the encoder
    // sleeps with a frame parked in the slot. Reading the channel after clearing the
    // slot closes that window, and DropWrite keeps a burst of offers from queueing
    // signals that outlive the frames they were for.
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _encoderLoop;
    private readonly Lock _subscriberGate = new();
    private readonly List<Subscription> _subscribers = [];

    private readonly ConcurrentQueue<Pending> _discarded = new();

    private Pending? _pending;
    private EncodedFrame? _latest;
    private long _sequence;
    private int _viewers;
    private DateTimeOffset _lastAccepted = DateTimeOffset.MinValue;

    /// <param name="clone">Copies a frame's pixels; returns null when it has none.</param>
    /// <param name="encode">Encodes a clone.</param>
    /// <param name="dispose">Frees a clone, encoded or dropped.</param>
    /// <param name="maxFps">Ceiling on frames accepted per second.</param>
    /// <param name="time">Injected for tests; defaults to the system clock.</param>
    public FrameBroadcaster(
        FrameCloner clone,
        FrameEncoder encode,
        FrameDisposer dispose,
        double maxFps,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(dispose);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFps);

        _clone = clone;
        _encode = encode;
        _dispose = dispose;
        _minInterval = TimeSpan.FromSeconds(1 / maxFps);
        _time = time ?? TimeProvider.System;

        _encoderLoop = Task.Run(() => RunEncoderAsync(_shutdown.Token), CancellationToken.None);
    }

    /// <summary>Whether anyone is currently subscribed.</summary>
    public bool HasViewers => Volatile.Read(ref _viewers) > 0;

    public int ViewerCount => Volatile.Read(ref _viewers);

    /// <summary>The most recently encoded frame, or null when none has been yet.</summary>
    public EncodedFrame? Latest => Volatile.Read(ref _latest);

    /// <summary>
    /// Offers a frame for encoding. Called on the capture thread; returns immediately
    /// and never blocks, drops the frame rather than waiting, and does nothing at all
    /// when no one is watching.
    /// </summary>
    public void Offer(Frame frame, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(detections);

        // Cheapest check first: with nobody watching this is the entire cost.
        if (Volatile.Read(ref _viewers) == 0) return;

        var now = _time.GetUtcNow();
        if (now - _lastAccepted < _minInterval) return;

        var clone = _clone(frame);
        if (clone is null) return;

        _lastAccepted = now;

        var pending = new Pending(clone, detections, frame.Width, frame.Height, now);

        // Latest wins. Whatever the encoder had not started on is dropped here.
        var displaced = Interlocked.Exchange(ref _pending, pending);
        if (displaced is not null)
        {
            // Handed to the encoder thread to free, never freed here: this runs on
            // the capture thread inside the snapshot gate, so a native release - or
            // worse, a disposer that blocks - would stall inference itself.
            _discarded.Enqueue(displaced);
        }

        // Signalled either way, and never blocking: a signal already pending is
        // dropped, because one wake is all it takes to pick up the newest frame and
        // drain the queue.
        _signal.Writer.TryWrite(0);
    }

    /// <summary>
    /// Subscribes to the stream. Dispose the result to unsubscribe - failing to is
    /// what keeps the encoder running after the last viewer has gone.
    /// </summary>
    public Subscription Subscribe()
    {
        var subscription = new Subscription(this);

        lock (_subscriberGate) _subscribers.Add(subscription);
        Interlocked.Increment(ref _viewers);

        return subscription;
    }

    private void Unsubscribe(Subscription subscription)
    {
        bool removed;
        lock (_subscriberGate) removed = _subscribers.Remove(subscription);

        if (removed) Interlocked.Decrement(ref _viewers);
    }

    private async Task RunEncoderAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            DrainDiscards();

            // Take the slot first, then wait only if it was empty. Doing it in this
            // order means a frame offered concurrently is either taken here or has
            // left a signal that the wait below returns on immediately - it can never
            // be both missed and unsignalled.
            var pending = Interlocked.Exchange(ref _pending, null);
            if (pending is null)
            {
                try
                {
                    await _signal.Reader.ReadAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                {
                    break;
                }

                continue;
            }

            byte[]? jpeg;
            try
            {
                jpeg = _encode(pending.Pixels, pending.Detections);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A frame that will not encode is a frame to skip. Taking the loop
                // down would take every viewer with it, and the pipeline itself is
                // unaffected either way.
                jpeg = null;
            }
            finally
            {
                // The encoder owns the clone and frees it, encoded or not.
                Free(pending);
            }

            if (jpeg is null || jpeg.Length == 0) continue;

            var encoded = new EncodedFrame(
                Interlocked.Increment(ref _sequence),
                jpeg,
                pending.Width,
                pending.Height,
                pending.Detections,
                pending.CapturedUtc);

            Volatile.Write(ref _latest, encoded);
            WakeSubscribers();
        }

        // Drain: whatever is still queued or in the slot is native memory nobody
        // else will free.
        DrainDiscards();

        var leftover = Interlocked.Exchange(ref _pending, null);
        if (leftover is not null) Free(leftover);
    }

    private void DrainDiscards()
    {
        while (_discarded.TryDequeue(out var dropped)) Free(dropped);
    }

    private void WakeSubscribers()
    {
        Subscription[] waiting;
        lock (_subscriberGate) waiting = [.. _subscribers];

        foreach (var subscriber in waiting) subscriber.Wake();
    }

    private void Free(Pending pending)
    {
        try
        {
            _dispose(pending.Pixels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed free is a leak, not a reason to stop serving frames.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _encoderLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        Subscription[] remaining;
        lock (_subscriberGate) remaining = [.. _subscribers];
        foreach (var subscriber in remaining) subscriber.Wake();

        _shutdown.Dispose();
        _signal.Writer.TryComplete();
    }

    private sealed record Pending(
        object Pixels,
        IReadOnlyList<Detection> Detections,
        int Width,
        int Height,
        DateTimeOffset CapturedUtc);

    /// <summary>
    /// One viewer's view of the stream: always the newest frame, never a backlog.
    /// </summary>
    public sealed class Subscription : IDisposable
    {
        private readonly FrameBroadcaster _owner;
        private readonly Lock _gate = new();

        private TaskCompletionSource<bool>? _waiter;
        private long _lastSeen;
        private bool _disposed;

        internal Subscription(FrameBroadcaster owner) => _owner = owner;

        /// <summary>
        /// The newest frame this subscriber has not seen, waiting for one if needed.
        /// Returns null when the broadcaster is shutting down or this subscription
        /// has been disposed.
        /// </summary>
        /// <remarks>
        /// Frames that arrive while the caller is busy writing to a socket are not
        /// queued: the next call returns the current newest and the intervening ones
        /// are simply skipped. That is what keeps one slow viewer from affecting
        /// anyone else.
        /// </remarks>
        public async ValueTask<EncodedFrame?> NextAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Volatile.Read(ref _disposed)) return null;

                var latest = _owner.Latest;
                if (latest is not null && latest.Sequence > _lastSeen)
                {
                    _lastSeen = latest.Sequence;
                    return latest;
                }

                TaskCompletionSource<bool> waiter;
                lock (_gate)
                {
                    // Re-check inside the lock: a frame can land between the read
                    // above and the waiter being installed, and without this the
                    // subscriber would sleep through it.
                    var newest = _owner.Latest;
                    if (newest is not null && newest.Sequence > _lastSeen)
                    {
                        _lastSeen = newest.Sequence;
                        return newest;
                    }

                    if (_disposed) return null;

                    // RunContinuationsAsynchronously is load-bearing: without it this
                    // viewer's socket write resumes on the encoder thread, and every
                    // other viewer waits behind it.
                    _waiter ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    waiter = _waiter;
                }

                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false),
                    waiter);

                await waiter.Task.ConfigureAwait(false);
            }

            return null;
        }

        internal void Wake()
        {
            TaskCompletionSource<bool>? waiter;
            lock (_gate)
            {
                waiter = _waiter;
                _waiter = null;
            }

            waiter?.TrySetResult(true);
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed)) return;

            Volatile.Write(ref _disposed, true);
            _owner.Unsubscribe(this);
            Wake();
        }
    }
}
