namespace SokagimdakiSorun.IntegrationTests.Database.Support;

/// <summary>Testte elle ilerletilen saat (ARCHITECTURE §12: zaman sabitlenir, <c>sleep</c> yok).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
