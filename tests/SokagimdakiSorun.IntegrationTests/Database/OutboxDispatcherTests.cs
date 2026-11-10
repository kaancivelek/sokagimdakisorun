using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.IntegrationTests.Database.SampleModule;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.Platform;
using SokagimdakiSorun.Platform.Outbox;

namespace SokagimdakiSorun.IntegrationTests.Database;

/// <summary>
/// WS-03: <see cref="OutboxDispatcher"/> — teslim, backoff ile yeniden deneme, dead-letter, <c>SKIP LOCKED</c>
/// ile eşzamanlı dağıtıcılar ve kontrollü kapanış. Gerçek PostgreSQL 18; zaman <see cref="ManualTimeProvider"/> ile.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class OutboxDispatcherTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delivers_event_to_handler_and_marks_row_processed()
    {
        var (connectionString, probe, time, services) = await ArrangeAsync();
        await using var _ = services;
        var integrationEvent = await SeedAsync(connectionString, WidgetCreated.New(Start, note: "lamba yanmıyor"));

        Assert.Equal(1, await CreateDispatcher(services).DispatchBatchAsync(Ct));

        var received = Assert.Single(probe.Received);
        Assert.Equal(integrationEvent, received);
        var row = await LoadSingleAsync(connectionString);
        Assert.Equal(time.GetUtcNow(), row.ProcessedAt);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.DeadLetteredAt);
        Assert.Equal(0, await CreateDispatcher(services).DispatchBatchAsync(Ct));
    }

    [Fact]
    public async Task Failed_handler_is_retried_after_backoff_then_succeeds()
    {
        var (connectionString, probe, time, services) = await ArrangeAsync();
        await using var _ = services;
        await SeedAsync(connectionString, WidgetCreated.New(Start));
        var calls = 0;
        probe.Behavior = (_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("geçici hata")
            : Task.CompletedTask;
        var dispatcher = CreateDispatcher(services);

        Assert.Equal(1, await dispatcher.DispatchBatchAsync(Ct));
        var failed = await LoadSingleAsync(connectionString);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Null(failed.ProcessedAt);
        Assert.Equal("System.InvalidOperationException: geçici hata", failed.LastError);
        // Deneme 1: taban gecikme 10 sn, "equal jitter" → [5 sn, 10 sn].
        Assert.InRange(failed.AvailableAt - time.GetUtcNow(), BaseRetryDelay / 2, BaseRetryDelay);

        // Backoff süresi dolmadan yeniden alınmaz.
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(Ct));

        time.Advance(BaseRetryDelay);
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(Ct));
        var processed = await LoadSingleAsync(connectionString);
        Assert.NotNull(processed.ProcessedAt);
        Assert.Equal(1, processed.AttemptCount);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Row_is_dead_lettered_after_max_attempts_and_not_dispatched_again()
    {
        var (connectionString, probe, time, services) = await ArrangeAsync(maxAttempts: 3);
        await using var _ = services;
        await SeedAsync(connectionString, WidgetCreated.New(Start));
        var calls = 0;
        probe.Behavior = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("kalıcı hata");
        };
        var dispatcher = CreateDispatcher(services);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(1, await dispatcher.DispatchBatchAsync(Ct));
            Assert.Equal(attempt, (await LoadSingleAsync(connectionString)).AttemptCount);
            time.Advance(MaxRetryDelay);
        }

        var row = await LoadSingleAsync(connectionString);
        Assert.Equal(3, calls);
        Assert.NotNull(row.DeadLetteredAt);
        Assert.Null(row.ProcessedAt);
        Assert.StartsWith("System.InvalidOperationException: kalıcı hata", row.LastError, StringComparison.Ordinal);

        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(Ct));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Event_without_registered_handler_is_marked_processed()
    {
        var (connectionString, _, _, services) = await ArrangeAsync(registerHandler: false);
        await using var _ = services;
        await SeedAsync(connectionString, WidgetCreated.New(Start));

        Assert.Equal(1, await CreateDispatcher(services).DispatchBatchAsync(Ct));

        Assert.NotNull((await LoadSingleAsync(connectionString)).ProcessedAt);
    }

    [Fact]
    public async Task Two_concurrent_dispatchers_never_process_the_same_row()
    {
        const int messageCount = 10;
        var (connectionString, probe, _, services) = await ArrangeAsync(batchSize: 5);
        await using var _ = services;
        await SeedAsync(connectionString, Enumerable.Range(0, messageCount).Select(i => WidgetCreated.New(Start.AddSeconds(-i))).ToArray());

        // İki dağıtıcı aynı anda satır kilitli tutarken handler içinde buluşur. FOR UPDATE SKIP LOCKED olmasaydı:
        // kilitsiz okumada aynı satırlar iki kez işlenir; SKIP'siz kilitte ikinci dağıtıcı birinciyi bekler ve buluşma
        // hiç gerçekleşmez (test zaman aşımıyla kırılır).
        var inFlight = 0;
        var bothDispatchersInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Behavior = async (_, ct) =>
        {
            if (Interlocked.Increment(ref inFlight) >= 2)
            {
                bothDispatchersInside.TrySetResult();
            }

            await bothDispatchersInside.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Interlocked.Decrement(ref inFlight);
        };

        var fetched = await Task.WhenAll(
            Task.Run(() => DrainAsync(CreateDispatcher(services)), Ct),
            Task.Run(() => DrainAsync(CreateDispatcher(services)), Ct));

        Assert.True(bothDispatchersInside.Task.IsCompletedSuccessfully);
        Assert.Equal(messageCount, fetched.Sum());
        Assert.All(fetched, count => Assert.True(count > 0, "Her iki dağıtıcı da satır almalıydı."));
        Assert.Equal(messageCount, probe.Received.Count);
        Assert.Equal(messageCount, probe.Received.Select(e => e.EventId).Distinct().Count());
        await using var platform = PostgresFixture.CreatePlatformContext(connectionString);
        Assert.Equal(messageCount, await platform.OutboxMessages.CountAsync(m => m.ProcessedAt != null && m.AttemptCount == 0, Ct));
    }

    [Fact]
    public async Task Stopping_the_host_cancels_in_flight_handler_without_counting_an_attempt()
    {
        var (connectionString, probe, _, services) = await ArrangeAsync();
        await using var _ = services;
        await SeedAsync(connectionString, WidgetCreated.New(Start));
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Behavior = async (_, ct) =>
        {
            handlerEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        using var dispatcher = CreateDispatcher(services);

        await dispatcher.StartAsync(Ct);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await dispatcher.StopAsync(Ct);

        Assert.True(dispatcher.ExecuteTask!.IsCompletedSuccessfully);
        var row = await LoadSingleAsync(connectionString);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.ProcessedAt);
        Assert.Null(row.DeadLetteredAt);
        Assert.Null(row.LastError);
    }

    private static async Task<int> DrainAsync(OutboxDispatcher dispatcher)
    {
        var total = 0;
        int fetched;
        while ((fetched = await dispatcher.DispatchBatchAsync(Ct)) > 0)
        {
            total += fetched;
        }

        return total;
    }

    private async Task<(string ConnectionString, HandlerProbe Probe, ManualTimeProvider Time, ServiceProvider Services)> ArrangeAsync(
        int maxAttempts = 5, int batchSize = 20, bool registerHandler = true)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var probe = new HandlerProbe();
        var time = new ManualTimeProvider(Start);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Outbox:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Outbox:BatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Outbox:BaseRetryDelay"] = BaseRetryDelay.ToString(),
            ["Outbox:MaxRetryDelay"] = MaxRetryDelay.ToString(),
            ["Outbox:PollingInterval"] = "00:00:00.050",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(probe);
        services.AddPlatform(configuration);
        if (registerHandler)
        {
            services.AddIntegrationEventHandler<WidgetCreated, ProbeHandler>();
        }

        return (connectionString, probe, time, services.BuildServiceProvider(validateScopes: true));
    }

    private static OutboxDispatcher CreateDispatcher(IServiceProvider services) =>
        ActivatorUtilities.CreateInstance<OutboxDispatcher>(services);

    private static async Task<WidgetCreated> SeedAsync(string connectionString, params WidgetCreated[] events)
    {
        await using var module = SampleModuleDbContext.Create(connectionString);
        foreach (var integrationEvent in events)
        {
            module.AddIntegrationEvent(integrationEvent);
        }

        await module.SaveChangesAsync(Ct);
        return events[0];
    }

    private static async Task<OutboxMessage> LoadSingleAsync(string connectionString)
    {
        await using var platform = PostgresFixture.CreatePlatformContext(connectionString);
        return await platform.OutboxMessages.AsNoTracking().SingleAsync(Ct);
    }

    internal sealed class HandlerProbe
    {
        public ConcurrentQueue<WidgetCreated> Received { get; } = new();

        public Func<WidgetCreated, CancellationToken, Task> Behavior { get; set; } = (_, _) => Task.CompletedTask;
    }

    internal sealed class ProbeHandler(HandlerProbe probe) : IIntegrationEventHandler<WidgetCreated>
    {
        public async Task HandleAsync(WidgetCreated integrationEvent, CancellationToken cancellationToken)
        {
            await probe.Behavior(integrationEvent, cancellationToken);
            probe.Received.Enqueue(integrationEvent);
        }
    }
}
