using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SokagimdakiSorun.IntegrationTests.Database.SampleModule;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.Platform.Outbox;
using static SokagimdakiSorun.IntegrationTests.Database.MigrationAndConventionTests;

namespace SokagimdakiSorun.IntegrationTests.Database;

/// <summary>
/// WS-03: modül, outbox satırını iş verisiyle aynı transaction'da yazar; rollback'te ikisi de yoktur (ADR-0002 §3).
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class OutboxWriteTests(PostgresFixture postgres)
{
    private const string CountWidgets = "SELECT count(*) FROM sample_module.widgets";
    private const string CountOutbox = "SELECT count(*) FROM platform.outbox_messages";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Business_row_and_outbox_row_are_committed_together()
    {
        var connectionString = await CreateModuleDatabaseAsync();
        var integrationEvent = WidgetCreated.New(DateTimeOffset.UtcNow, note: "çukur");

        await using (var module = SampleModuleDbContext.Create(connectionString))
        {
            module.Widgets.Add(NewWidget(integrationEvent.WidgetId));
            module.AddIntegrationEvent(integrationEvent);
            await module.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await CountAsync(connectionString, CountWidgets));
        await using var platform = PostgresFixture.CreatePlatformContext(connectionString);
        var row = await platform.OutboxMessages.SingleAsync(Ct);
        Assert.Equal(integrationEvent.EventId, row.Id);
        Assert.Equal(typeof(WidgetCreated).FullName, row.Type);
        // jsonb gövdeyi yeniden biçimlendirir; içerik eşitliği JSON olarak karşılaştırılır.
        using var payload = System.Text.Json.JsonDocument.Parse(row.Payload);
        Assert.Equal("çukur", payload.RootElement.GetProperty("note").GetString());
        Assert.Equal(integrationEvent.WidgetId, payload.RootElement.GetProperty("widgetId").GetGuid());
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.ProcessedAt);
    }

    [Fact]
    public async Task Rolled_back_transaction_leaves_neither_business_row_nor_outbox_row()
    {
        var connectionString = await CreateModuleDatabaseAsync();

        await using (var module = SampleModuleDbContext.Create(connectionString))
        {
            await using var transaction = await module.Database.BeginTransactionAsync(Ct);
            var integrationEvent = WidgetCreated.New(DateTimeOffset.UtcNow);
            module.Widgets.Add(NewWidget(integrationEvent.WidgetId));
            module.AddIntegrationEvent(integrationEvent);
            await module.SaveChangesAsync(Ct);

            // Transaction içinde ikisi de görünür (aynı bağlantı)...
            Assert.Equal(1, await module.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM platform.outbox_messages").SingleAsync(Ct));

            await transaction.RollbackAsync(Ct);
        }

        // ...rollback sonrası hiçbiri yok.
        Assert.Equal(0, await CountAsync(connectionString, CountWidgets));
        Assert.Equal(0, await CountAsync(connectionString, CountOutbox));
    }

    [Fact]
    public async Task Failed_save_writes_no_outbox_row()
    {
        var connectionString = await CreateModuleDatabaseAsync();
        var existingId = Guid.CreateVersion7();
        await using (var module = SampleModuleDbContext.Create(connectionString))
        {
            module.Widgets.Add(NewWidget(existingId));
            await module.SaveChangesAsync(Ct);
        }

        await using (var module = SampleModuleDbContext.Create(connectionString))
        {
            module.AddIntegrationEvent(WidgetCreated.New(DateTimeOffset.UtcNow));
            module.Widgets.Add(NewWidget(existingId)); // birincil anahtar çakışması → tüm SaveChanges geri alınır

            await Assert.ThrowsAsync<DbUpdateException>(() => module.SaveChangesAsync(Ct));
        }

        Assert.Equal(1, await CountAsync(connectionString, CountWidgets));
        Assert.Equal(0, await CountAsync(connectionString, CountOutbox));
    }

    [Fact]
    public async Task Module_context_cannot_modify_or_delete_outbox_rows()
    {
        var connectionString = await CreateModuleDatabaseAsync();
        await using (var module = SampleModuleDbContext.Create(connectionString))
        {
            module.AddIntegrationEvent(WidgetCreated.New(DateTimeOffset.UtcNow));
            await module.SaveChangesAsync(Ct);
        }

        await using (var module = SampleModuleDbContext.Create(connectionString))
        {
            var row = await module.Set<OutboxMessage>().SingleAsync(Ct);
            row.AttemptCount = 99;
            await Assert.ThrowsAsync<InvalidOperationException>(() => module.SaveChangesAsync(Ct));

            module.ChangeTracker.Clear();
            module.Remove(await module.Set<OutboxMessage>().SingleAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => module.SaveChangesAsync(Ct));
        }

        Assert.Equal(1, await CountAsync(connectionString, "SELECT count(*) FROM platform.outbox_messages WHERE attempt_count = 0"));
    }

    private async Task<string> CreateModuleDatabaseAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var module = SampleModuleDbContext.Create(connectionString);
        await module.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
        return connectionString;
    }

    private static Widget NewWidget(Guid id) =>
        new() { Id = id, DisplayName = "lamba", Color = WidgetColor.Red, CreatedAt = DateTimeOffset.UtcNow };
}
