namespace StaticHost.Tests.Live;

public sealed class LiveStatusBroadcasterTests
{
    private static (LiveStatusBroadcaster Broadcaster, FakeTimeProvider Time) Create(int coalesceMs = 750)
    {
        var time = new FakeTimeProvider(startDateTime: DateTimeOffset.UnixEpoch);
        var opts = Options.Create(new LiveStatusOptions { CoalesceWindowMs = coalesceMs });
        var b = new LiveStatusBroadcaster(opts, NullLogger<LiveStatusBroadcaster>.Instance, time);
        return (b, time);
    }

    [Fact]
    public void Update_SetsPrimaryToFirstLiveSource()
    {
        var (b, time) = Create();
        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "aspiredotdev", null) });
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(b.Current.IsLive);
        Assert.Equal("twitch", b.Current.PrimarySource);
        Assert.False(string.IsNullOrEmpty(b.Current.LiveSessionId));
    }

    [Fact]
    public void Update_KeepsPrimaryAndLiveSessionWhenSecondSourceJoins()
    {
        var (b, time) = Create();

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "aspiredotdev", null) });
        time.Advance(TimeSpan.FromSeconds(1));
        var firstSessionId = b.Current.LiveSessionId;

        b.Update(new LiveStatusUpdate { YouTube = new YouTubeStatus(true, "abc123") });
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("twitch", b.Current.PrimarySource);
        Assert.Equal(firstSessionId, b.Current.LiveSessionId);
        Assert.True(b.Current.IsLive);
        Assert.True(b.Current.Twitch.Live);
        Assert.True(b.Current.YouTube.Live);
    }

    [Fact]
    public void Update_SwapsPrimaryToRemainingSourceWhenOriginalGoesOffline()
    {
        var (b, time) = Create();

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });
        time.Advance(TimeSpan.FromSeconds(1));
        var firstSessionId = b.Current.LiveSessionId;

        b.Update(new LiveStatusUpdate { YouTube = new YouTubeStatus(true, "v") });
        time.Advance(TimeSpan.FromSeconds(1));

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(false, null, null) });
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("youtube", b.Current.PrimarySource);
        Assert.True(b.Current.IsLive);
        Assert.Equal(firstSessionId, b.Current.LiveSessionId);
    }

    [Fact]
    public void Current_HasNoPrimarySourceWhenNothingIsLive()
    {
        var (b, _) = Create();
        Assert.Null(b.Current.PrimarySource);
        Assert.Null(b.Current.LiveSessionId);
        Assert.False(b.Current.IsLive);
    }

    [Fact]
    public void Update_RotatesLiveSessionIdAfterAllSourcesGoOffline()
    {
        var (b, time) = Create();

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });
        time.Advance(TimeSpan.FromSeconds(1));
        var firstSessionId = b.Current.LiveSessionId;

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(false, null, null) });
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(b.Current.LiveSessionId);

        time.Advance(TimeSpan.FromMinutes(1));
        b.Update(new LiveStatusUpdate { YouTube = new YouTubeStatus(true, "v") });
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.NotEqual(firstSessionId, b.Current.LiveSessionId);
    }

    [Fact]
    public async Task Update_CoalescesBurstIntoSingleEvent()
    {
        var (b, time) = Create(coalesceMs: 750);
        var (reader, sub) = b.Subscribe();

        var seeded = await reader.ReadAsync();
        Assert.False(seeded.Snapshot.IsLive);

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });
        b.Update(new LiveStatusUpdate { YouTube = new YouTubeStatus(true, "v") });

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(reader.TryRead(out _));

        time.Advance(TimeSpan.FromMilliseconds(800));

        var combined = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(combined.Snapshot.IsLive);
        Assert.True(combined.Snapshot.Twitch.Live);
        Assert.True(combined.Snapshot.YouTube.Live);
        Assert.False(string.IsNullOrEmpty(combined.Snapshot.LiveSessionId));
        Assert.False(reader.TryRead(out _));

        sub.Dispose();
    }

    [Fact]
    public void Update_DoesNotScheduleBroadcastForNoOp()
    {
        var (b, time) = Create(coalesceMs: 100);
        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(false, null, null) });
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(LiveStatus.Idle.IsLive, b.Current.IsLive);
        Assert.Null(b.Current.PrimarySource);
    }

    [Fact]
    public async Task Subscribe_SeedsWithCurrentSnapshot()
    {
        var (b, _) = Create();
        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });
        b.FlushNow();

        var (reader, sub) = b.Subscribe();
        var first = await reader.ReadAsync();
        Assert.True(first.Snapshot.IsLive);
        sub.Dispose();
    }

    [Fact]
    public void Update_KeepsFirstSourcePrimaryWhenSecondJoinsWithinCoalesceWindow()
    {
        // Regression: while a going-live burst is still coalescing, the source that
        // arrived first must stay primary. Resolving against _current (still Idle)
        // instead of the pending basis let the second source steal primary.
        var (b, time) = Create(coalesceMs: 750);

        b.Update(new LiveStatusUpdate { YouTube = new YouTubeStatus(true, "v") });
        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("youtube", b.Current.PrimarySource);
        Assert.True(b.Current.Twitch.Live);
        Assert.True(b.Current.YouTube.Live);
    }

    [Fact]
    public async Task Update_WithIdenticalSubstantiveState_DoesNotBroadcastAgain()
    {
        // Regression: an ever-advancing UpdatedAt must not turn a no-change reconcile
        // into a redundant SSE event.
        var (b, time) = Create(coalesceMs: 0);
        var (reader, sub) = b.Subscribe();

        await reader.ReadAsync(); // seed

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });
        var first = await reader.ReadAsync();
        Assert.True(first.Snapshot.IsLive);

        time.Advance(TimeSpan.FromSeconds(5));
        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });

        Assert.False(reader.TryRead(out _));

        sub.Dispose();
    }

    [Fact]
    public async Task Unsubscribe_StopsDeliveringEvents()
    {
        var (b, time) = Create(coalesceMs: 1);
        var (reader, sub) = b.Subscribe();
        await reader.ReadAsync();

        sub.Dispose();

        b.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "x", null) });
        time.Advance(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<ChannelClosedException>(async () => await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Subscribers_ShareOneSerializedFramePerPublishedSnapshot()
    {
        using var broadcaster = LiveTestHelpers.CreateBroadcaster();
        var (first, firstSubscription) = broadcaster.Subscribe();
        var (second, secondSubscription) = broadcaster.Subscribe();
        using var firstLease = firstSubscription;
        using var secondLease = secondSubscription;
        var idle = await first.ReadAsync();
        Assert.Same(idle, await second.ReadAsync());

        broadcaster.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "aspiredotdev", "Live") });
        var update = await first.ReadAsync();
        Assert.Same(update, await second.ReadAsync());
        Assert.NotSame(idle, update);
        var expected = $"event: state\ndata: {JsonSerializer.Serialize(broadcaster.Current, LiveStatusJsonContext.Default.LiveStatus)}\n\n";
        Assert.Equal(expected, Encoding.UTF8.GetString(update.Frame.Span));

        var (late, lateSubscription) = broadcaster.Subscribe();
        using var lateLease = lateSubscription;
        Assert.Same(update, await late.ReadAsync());
        broadcaster.Update(new LiveStatusUpdate { Twitch = new TwitchStatus(true, "aspiredotdev", "Live") });
        Assert.False(first.TryRead(out _));
        Assert.False(second.TryRead(out _));
    }

    [Fact]
    public void SubscriberChurn_DoesNotAllocateCopiesOfTheAudience()
    {
        using var broadcaster = LiveTestHelpers.CreateBroadcaster();
        var leases = new List<IDisposable> { broadcaster.Subscribe().Unsubscribe };
        var smallAudienceBytes = MeasureChurn(broadcaster);
        for (var i = 0; i < 512; i++)
        {
            leases.Add(broadcaster.Subscribe().Unsubscribe);
        }
        var largeAudienceBytes = MeasureChurn(broadcaster);
        Assert.True(largeAudienceBytes <= smallAudienceBytes + 16_384,
            $"100 connections allocated {smallAudienceBytes} bytes with one viewer and {largeAudienceBytes} bytes with 513 viewers.");
        foreach (var lease in leases) lease.Dispose();
    }

    private static long MeasureChurn(LiveStatusBroadcaster broadcaster)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            var (_, subscription) = broadcaster.Subscribe();
            subscription.Dispose();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
