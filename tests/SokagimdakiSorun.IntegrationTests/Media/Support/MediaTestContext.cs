using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.Media;
using SokagimdakiSorun.Media.Infrastructure;

namespace SokagimdakiSorun.IntegrationTests.Media.Support;

/// <summary>
/// Media modülünü gerçek PostgreSQL 18 (test başına boş veritabanı, <c>media</c> migration'ı uygulanmış) ve test başına
/// geçici bir medya kökü ile kurar. Saat elle ilerletilir; loglar <see cref="Logs"/>'ta toplanır.
/// </summary>
internal sealed class MediaTestContext : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _sandbox;

    private MediaTestContext(ServiceProvider provider, string connectionString, string sandbox, string root, ManualTimeProvider clock, ListLoggerProvider logs)
    {
        _provider = provider;
        ConnectionString = connectionString;
        _sandbox = sandbox;
        Root = root;
        Clock = clock;
        Logs = logs;
    }

    public string ConnectionString { get; }

    public string Root { get; }

    public ManualTimeProvider Clock { get; }

    public ListLoggerProvider Logs { get; }

    public static async Task<MediaTestContext> CreateAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var sandbox = Path.Combine(Path.GetTempPath(), "sokagimdakisorun-tests", Guid.CreateVersion7().ToString("N"));
        var root = Path.Combine(sandbox, "media");

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Media:RootPath"] = root,
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        // timestamptz mikrosaniye hassasiyetindedir; saat bu hassasiyete yuvarlanır ki okunan değer yazılanla aynı olsun.
        var start = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(start.AddTicks(-(start.Ticks % 10)));
        var logs = new ListLoggerProvider();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<TimeProvider>(clock);
        services.AddMediaModule(configuration);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var context = new MediaTestContext(provider, connectionString, sandbox, root, clock, logs);
        await using var scope = context.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MediaDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
        return context;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public T GetRequiredService<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>Kökteki tüm dosyalar (göreli yol).</summary>
    public IReadOnlyList<string> Files() =>
        Directory.Exists(Root)
            ? [.. Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root, f)).Order(StringComparer.Ordinal)]
            : [];

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }
}

/// <summary>Tüm log kayıtlarını (mesaj + exception metni) toplar; sızıntı kontrolü için.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new ListLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class ListLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
