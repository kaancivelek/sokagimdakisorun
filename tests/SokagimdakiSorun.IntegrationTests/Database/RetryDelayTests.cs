using SokagimdakiSorun.Platform.Outbox;

namespace SokagimdakiSorun.IntegrationTests.Database;

/// <summary>Backoff hesabı: üstel büyür, üst sınırı aşmaz, jitter yarım aralıkta kalır. Veritabanı gerektirmez.</summary>
public sealed class RetryDelayTests
{
    private static readonly OutboxOptions Options = new()
    {
        BaseRetryDelay = TimeSpan.FromSeconds(5),
        MaxRetryDelay = TimeSpan.FromMinutes(1),
    };

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(50, 60)]
    public void Delay_grows_exponentially_with_equal_jitter_and_is_capped(int attempt, int expectedCapSeconds)
    {
        var cap = TimeSpan.FromSeconds(expectedCapSeconds);

        Assert.Equal(cap / 2, OutboxDispatcher.ComputeRetryDelay(attempt, Options, jitterSample: 0));
        Assert.Equal(cap, OutboxDispatcher.ComputeRetryDelay(attempt, Options, jitterSample: 1));
        Assert.InRange(OutboxDispatcher.ComputeRetryDelay(attempt, Options, jitterSample: 0.5), cap / 2, cap);
    }
}
