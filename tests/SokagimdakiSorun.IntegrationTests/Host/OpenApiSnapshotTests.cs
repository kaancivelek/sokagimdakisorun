using System.Net;
using System.Text.Json.Nodes;

namespace SokagimdakiSorun.IntegrationTests.Host;

/// <summary>
/// Koddan üretilen OpenAPI belgesi ile commit'li anlık görüntü (docs/api/openapi.v1.json)
/// aynı olmalıdır (docs/API_CONTRACT_STRATEGY.md §1 adım 4).
/// </summary>
public sealed class OpenApiSnapshotTests
{
    private const string SnapshotRelativePath = "docs/api/openapi.v1.json";

    [Fact]
    public async Task Generated_document_matches_committed_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = HostFactory.Create("Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var generated = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));

        var snapshotPath = Path.Combine(FindRepositoryRoot(), SnapshotRelativePath);
        Assert.True(File.Exists(snapshotPath), $"Anlık görüntü bulunamadı: {snapshotPath}. 'dotnet build' ile üretin.");
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath, ct));

        Assert.True(
            JsonNode.DeepEquals(generated, committed),
            $"{SnapshotRelativePath} güncel değil. 'dotnet build' ile yeniden üretip değişikliği commit edin.\n"
            + $"Üretilen:\n{generated?.ToJsonString()}\nAnlık görüntü:\n{committed?.ToJsonString()}");
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SokagimdakiSorun.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Depo kökü (SokagimdakiSorun.slnx) bulunamadı.");
    }
}
