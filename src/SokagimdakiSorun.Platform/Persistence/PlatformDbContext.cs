using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Platform.Outbox;

namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>
/// <c>platform</c> schema'sının bağlamı. <c>platform.outbox_messages</c> tablosunun migration'ına sahip
/// olan ve satırlarını okuyup güncelleyebilen tek bağlamdır (ADR-0002 §3; yalnızca <see cref="OutboxDispatcher"/> kullanır).
/// </summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "platform";

    public override string Schema => SchemaName;

    internal override bool OwnsOutbox => true;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
}
