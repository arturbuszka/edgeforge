using EdgeForge.Core;
using EdgeForge.Runtime;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The fan-out from one capture loop to many viewers. Every test here defends a
/// property the live view depends on and that is painful to diagnose in the field:
/// that nobody watching costs nothing, that a slow phone cannot stall the pipeline
/// or another viewer, and that a dropped frame is freed exactly once.
/// </summary>
/// <remarks>
/// The clone, encode and free steps are injected, so these run without OpenCV: a
/// clone is a counter object and an encode is a byte array. That keeps the tests
/// about the concurrency, which is where the risk actually is.
/// </remarks>
public class FrameBroadcasterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>A stand-in for a Mat, so a leak or a double free is observable.</summary>
    private sealed class FakeClone
    {
        public bool Freed { get; private set; }

        public void Free()
        {
            // A double free on a real Mat corrupts the heap rather than throwing,
            // so the stand-in makes it loud.
            Assert.False(Freed);
            Freed = true;
        }
    }

    /// <summary>The production disposer's stand-in.</summary>
    private static void Free(object clone) => ((FakeClone)clone).Free();

    private static FakeClone Clone(Frame frame) => new();

    private static Frame AFrame(int index = 0) =>
        new($"CAM-{index:000000}", DateTimeOffset.UtcNow, 640, 480);

    private static IReadOnlyList<Detection> NoDetections => [];

    /// <summary>Advances on every read, so the fps throttle never rejects a frame.</summary>
    private sealed class RunningClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    /// <summary>A clock the test advances by hand, for the throttle.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task NoViewersMeansNoEncoding()
    {
        // The CPU-idle guarantee. With nobody watching, a running pipeline must not
        // clone or encode anything at all.
        var clones = 0;
        var encodes = 0;

        await using var broadcaster = new FrameBroadcaster(
            _ => { Interlocked.Increment(ref clones); return new FakeClone(); },
            (_, _) => { Interlocked.Increment(ref encodes); return [1]; },
            Free,
            maxFps: 1000,
            new RunningClock());

        for (var i = 0; i < 100; i++) broadcaster.Offer(AFrame(i), NoDetections);

        Assert.Equal(0, clones);
        Assert.Equal(0, encodes);
        Assert.False(broadcaster.HasViewers);
        Assert.Null(broadcaster.Latest);
    }

    [Fact]
    public async Task UnsubscribeReturnsToIdle()
    {
        // The leak this catches: a stream handler that forgets to dispose its
        // subscription keeps the encoder busy forever after the viewer has gone.
        var encodes = 0;

        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) => { Interlocked.Increment(ref encodes); return [1]; },
            Free,
            maxFps: 1000,
            new RunningClock());

        var subscription = broadcaster.Subscribe();
        Assert.Equal(1, broadcaster.ViewerCount);

        subscription.Dispose();
        Assert.Equal(0, broadcaster.ViewerCount);

        var before = Volatile.Read(ref encodes);
        for (var i = 0; i < 50; i++) broadcaster.Offer(AFrame(i), NoDetections);

        Assert.Equal(before, Volatile.Read(ref encodes));
    }

    [Fact]
    public async Task ASubscriberReceivesAnOfferedFrame()
    {
        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) => [0xFF, 0xD8, 0xFF],
            Free,
            maxFps: 1000,
            new RunningClock());

        using var subscription = broadcaster.Subscribe();

        broadcaster.Offer(AFrame(), NoDetections);

        using var cts = new CancellationTokenSource(Timeout);
        var frame = await subscription.NextAsync(cts.Token);

        Assert.NotNull(frame);
        Assert.Equal(640, frame!.Width);
        Assert.Equal(480, frame.Height);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, frame.Jpeg);
    }

    [Fact]
    public async Task ASlowSubscriberSkipsFramesRatherThanQueueing()
    {
        // A phone on bad Wi-Fi must see a lower framerate, not a growing backlog.
        var slowEncode = new ManualResetEventSlim(false);
        var encodes = 0;

        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) =>
            {
                Interlocked.Increment(ref encodes);

                // Hold the encoder so offers pile against a full slot, which is the
                // condition the drop policy exists for.
                slowEncode.Wait(TimeSpan.FromMilliseconds(50));
                return [1];
            },
            Free,
            maxFps: 1000,
            new RunningClock());

        using var subscription = broadcaster.Subscribe();

        // The subscriber never reads while these land, and the encoder is slow, so
        // the single slot is overwritten again and again.
        const int offered = 200;
        for (var i = 0; i < offered; i++) broadcaster.Offer(AFrame(i), NoDetections);

        slowEncode.Set();

        using var cts = new CancellationTokenSource(Timeout);
        var frame = await subscription.NextAsync(cts.Token);

        Assert.NotNull(frame);

        // The point: work is bounded by what the encoder can do, not by what the
        // camera produced. A queue would have encoded all 200.
        Assert.True(
            Volatile.Read(ref encodes) < offered,
            $"expected drops, but {Volatile.Read(ref encodes)} of {offered} frames were encoded");
    }

    [Fact]
    public async Task ASlowSubscriberDoesNotBlockAFastOne()
    {
        // One viewer that never reads must not stall another. This is what
        // RunContinuationsAsynchronously on the waiter buys.
        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) => [1],
            Free,
            maxFps: 1000,
            new RunningClock());

        using var idle = broadcaster.Subscribe();      // never calls NextAsync
        using var active = broadcaster.Subscribe();
        using var cts = new CancellationTokenSource(Timeout);

        for (var i = 0; i < 5; i++)
        {
            broadcaster.Offer(AFrame(i), NoDetections);

            var frame = await active.NextAsync(cts.Token);
            Assert.NotNull(frame);
        }
    }

    [Fact]
    public async Task EveryCloneIsFreedExactlyOnce()
    {
        // Latest-wins drops frames by design, and a dropped clone that is not freed
        // is a native memory leak that only shows up after hours of uptime. Freeing
        // one twice is worse: a double free on a Mat is not recoverable.
        var gate = new ManualResetEventSlim(false);
        var created = 0;
        var freed = 0;

        await using (var broadcaster = new FrameBroadcaster(
            _ => { Interlocked.Increment(ref created); return new FakeClone(); },
            (_, _) => { gate.Wait(Timeout); return [1]; },
            clone =>
            {
                Free(clone);                       // asserts against a second free
                Interlocked.Increment(ref freed);
            },
            maxFps: 1000,
            new RunningClock()))
        {
            using var subscription = broadcaster.Subscribe();

            for (var i = 0; i < 10; i++) broadcaster.Offer(AFrame(i), NoDetections);

            gate.Set();

            using var cts = new CancellationTokenSource(Timeout);
            Assert.NotNull(await subscription.NextAsync(cts.Token));
        }

        // DisposeAsync drains the discard queue and the pending slot, so by here
        // every clone the broadcaster made has been handed back exactly once.
        Assert.Equal(Volatile.Read(ref created), Volatile.Read(ref freed));
    }

    [Fact]
    public async Task OfferDoesNotBlockWhileTheEncoderIsBusy()
    {
        // Offer runs on the capture thread inside the snapshot gate. If it waits on
        // the encoder - or on the disposer, which is the subtler version of the same
        // mistake - then inference waits with it and detector throughput drops.
        var release = new ManualResetEventSlim(false);

        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) => { release.Wait(Timeout); return [1]; },
            _ => release.Wait(Timeout),
            maxFps: 1000,
            new RunningClock());

        using var subscription = broadcaster.Subscribe();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) broadcaster.Offer(AFrame(i), NoDetections);
        elapsed.Stop();

        release.Set();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"Offer blocked for {elapsed.Elapsed}, which would stall the capture loop");
    }

    [Fact]
    public async Task MaxFpsThrottlesOffers()
    {
        // The stream is capped well below camera framerate: it is for watching cats,
        // not for measuring latency, and every accepted frame costs an encode.
        var clones = 0;
        var clock = new SteppedClock();

        await using var broadcaster = new FrameBroadcaster(
            _ => { Interlocked.Increment(ref clones); return new FakeClone(); },
            (_, _) => [1],
            Free,
            maxFps: 10,
            clock);

        using var subscription = broadcaster.Subscribe();

        // One second of wall time, offered at 100 fps.
        for (var i = 0; i < 100; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            broadcaster.Offer(AFrame(i), NoDetections);
        }

        Assert.InRange(Volatile.Read(ref clones), 8, 12);
    }

    [Fact]
    public async Task CancellingNextAsyncDoesNotLeakTheSubscription()
    {
        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) => [1],
            Free,
            maxFps: 1000,
            new RunningClock());

        var subscription = broadcaster.Subscribe();

        using (var cts = new CancellationTokenSource())
        {
            var pending = subscription.NextAsync(cts.Token);
            await cts.CancelAsync();

            Assert.Null(await pending);
        }

        subscription.Dispose();

        Assert.Equal(0, broadcaster.ViewerCount);
    }

    [Fact]
    public async Task NextAsyncReturnsNullAfterDispose()
    {
        // The stream handler loops until this returns null; a hang here is a socket
        // that never closes.
        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) => [1],
            Free,
            maxFps: 1000,
            new RunningClock());

        var subscription = broadcaster.Subscribe();
        subscription.Dispose();

        using var cts = new CancellationTokenSource(Timeout);

        Assert.Null(await subscription.NextAsync(cts.Token));
    }

    [Fact]
    public async Task AFrameThatWillNotEncodeIsSkippedRatherThanKillingTheLoop()
    {
        // One bad frame must not end the stream for every viewer.
        var attempts = 0;
        var threw = new ManualResetEventSlim(false);

        await using var broadcaster = new FrameBroadcaster(
            Clone,
            (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    // Signalled before throwing, so the test can wait on the failure
                    // itself rather than polling a counter and hoping.
                    threw.Set();
                    throw new InvalidOperationException("encoder said no");
                }

                return [1];
            },
            Free,
            maxFps: 1000,
            new RunningClock());

        using var subscription = broadcaster.Subscribe();
        using var cts = new CancellationTokenSource(Timeout);

        // Let the first frame reach the encoder on its own, so it is the one that
        // throws. Offering a second immediately would displace it from the slot and
        // the throw would land on the newer frame instead.
        broadcaster.Offer(AFrame(0), NoDetections);
        Assert.True(threw.Wait(Timeout), "the first encode should have been attempted");

        // The loop survived: a later frame still encodes and still reaches the viewer.
        // One offer is enough - a lost wakeup here would be a real defect, not a
        // reason for the test to retry.
        broadcaster.Offer(AFrame(1), NoDetections);

        var frame = await subscription.NextAsync(cts.Token);

        Assert.NotNull(frame);
        Assert.True(
            Volatile.Read(ref attempts) >= 2,
            "the encoder loop should have kept running after the failed encode");
    }
}
