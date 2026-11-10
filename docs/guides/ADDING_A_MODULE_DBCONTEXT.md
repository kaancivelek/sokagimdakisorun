# Yeni modül `DbContext`'i nasıl eklenir

| | |
|---|---|
| Doküman durumu | Kabul edildi (WS-03) |
| Son güncelleme | 2026-11-11 |
| Bağlayıcı kaynak | [ADR-0002](../decisions/ADR-0002-postgresql-data-ownership.md), [ARCHITECTURE §4.2, §14](../ARCHITECTURE.md) |

Bu rehber Identity, Media, Complaints ve Messaging sohbetleri içindir. Platform tarafındaki altyapı (`src/SokagimdakiSorun.Platform/`) WS-03'te kuruldu; modüller yalnızca **kendi** projelerine dosya ekler.

## 1. Taban sınıftan türet

```csharp
// src/Modules/Complaints/SokagimdakiSorun.Complaints/Infrastructure/ComplaintsDbContext.cs
using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Platform.Persistence;

internal sealed class ComplaintsDbContext(DbContextOptions<ComplaintsDbContext> options) : ModuleDbContext(options)
{
    public override string Schema => "complaints";

    public DbSet<Complaint> Complaints => Set<Complaint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // ZORUNLU: schema + paylaşılan outbox eşlemesi
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ComplaintsDbContext).Assembly);
    }
}
```

`ModuleDbContext` şunları **kendiliğinden** yapar; modül tekrarlamaz:

| Konu | Davranış |
|---|---|
| Schema | Varsayılan schema = `Schema`. Başka schema'ya tablo eşlemeyin (outbox hariç, o da otomatik). |
| Migration geçmişi | `<schema>.__ef_migrations_history` |
| Adlandırma | Tablo, kolon, PK/FK/index adları `snake_case` (`EFCore.NamingConventions`, V-2). Tablo adını çoğul verin: `ToTable("complaints")` veya `DbSet` adı. |
| Zaman | `DateTimeOffset` → `timestamptz`. Npgsql yalnızca UTC kabul eder: `TimeProvider.GetUtcNow()` kullanın. `DateTime` kullanmayın. |
| Enum | Metin olarak saklanır; her enum kolonu için `ck_<tablo>_<kolon>` adlı `CHECK (… IN (…))` kısıtı otomatik eklenir. Enum'a değer eklemek migration gerektirir (kısıt değişir). |
| Outbox | `platform.outbox_messages` modele **migration dışı** eklenir; modül migration'ı bu tabloyu oluşturmaz. |

Kimlikler: entity'ler `Guid.CreateVersion7()` ile kendi kimliğini atar (ARCHITECTURE §3). Atanmamış `Guid` anahtarı için Npgsql sağlayıcısı da v7 üretir (testle doğrulandı), ama açık atama tercih edilir.

## 2. Kaydet

```csharp
// <Modül>Module.cs → Add<Modül>Module(...)
services.AddModuleDbContext<ComplaintsDbContext>();
```

- Bağlantı dizesi `ConnectionStrings__Default`'tan, bağlam ilk oluşturulduğunda okunur. Kayıt sırasında veritabanına bağlanılmaz (build-zamanı OpenAPI üretimi bozulmaz).
- Kayıt, bağlamı açılış migration listesine ekler: `Database__ApplyMigrationsOnStartup=true` iken host açılışında önce `platform`, sonra modüller kayıt sırasıyla migrate edilir.
- Host'un kendi kontrolü veritabanı bağlantısını zaten içerir; modül ayrıca health check eklemez.

## 3. Migration üret

Modül projesine tasarım-zamanı fabrikası ve `Microsoft.EntityFrameworkCore.Design` (yalnızca build) ekleyin:

```xml
<!-- Modül .csproj -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Design">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

```csharp
internal sealed class ComplaintsDesignTimeDbContextFactory : ModuleDesignTimeDbContextFactory<ComplaintsDbContext>
{
    protected override ComplaintsDbContext Create(DbContextOptions<ComplaintsDbContext> options) => new(options);
}
```

```bash
dotnet ef migrations add InitialComplaints \
  --project src/Modules/Complaints/SokagimdakiSorun.Complaints \
  --startup-project src/Modules/Complaints/SokagimdakiSorun.Complaints \
  --context ComplaintsDbContext --output-dir Infrastructure/Migrations
```

Migration üretimi veritabanına bağlanmaz ve API host'unu çalıştırmaz. Üretilen dosyayı **okuyun**: `CreateTable` yalnızca modülün schema'sında olmalı; `outbox_messages` veya `EnsureSchema("platform")` görünüyorsa `base.OnModelCreating` çağrılmamıştır. PR açıklamasında migration adını ve etkisini yazın (ADR-0002 §4).

SQL'i incelemek için: `dotnet ef migrations script --idempotent ...` (aynı parametrelerle).

## 4. Integration event yaz (outbox)

Event tipi modülün `Contracts` projesinde `IIntegrationEvent` uygular. Yazma, iş verisiyle **aynı** `SaveChangesAsync` çağrısındadır:

```csharp
complaint.Resolve(...);
db.AddIntegrationEvent(new ComplaintStatusChanged(Guid.CreateVersion7(), time.GetUtcNow(), ...));
await db.SaveChangesAsync(ct); // iş verisi + outbox satırı tek transaction
```

- `AddIntegrationEvent` yalnızca ekler; kaydetmez. Açık bir transaction kullanıyorsanız (`BeginTransactionAsync`) outbox satırı da ona dâhildir; rollback ikisini birden geri alır.
- Outbox satırının kimliği `EventId`'dir; aynı event iki kez eklenirse birincil anahtar ihlali olur.
- Modül outbox satırını okuyamaz, güncelleyemez, silemez: `OutboxMessage` tipi `internal`'dır ve `ModuleDbContext` değiştirme/silme girişimini `SaveChanges`'ta reddeder (ADR-0002 §3).

## 5. Event dinle (handler)

```csharp
internal sealed class ComplaintStatusChangedHandler(MessagingDbContext db) : IIntegrationEventHandler<ComplaintStatusChanged>
{
    public async Task HandleAsync(ComplaintStatusChanged e, CancellationToken ct) { /* idempotent */ }
}

// Add<Modül>Module:
services.AddIntegrationEventHandler<ComplaintStatusChanged, ComplaintStatusChangedHandler>();
```

Sözleşme (`IIntegrationEventHandler<T>` belgesinde ayrıntılı):

- **En az bir kez** teslim: handler idempotent olmalı. `EventId`'yi kendi tablonuzda `UNIQUE` bir anahtar olarak kullanın (ör. giden mesajda `dedup_key`).
- Bir event'in birden çok handler'ı varsa biri hata verdiğinde hepsi yeniden çağrılır.
- Handler yalnızca kendi modülünün verisini, kendi transaction'ında yazar; dış HTTP çağrısı yapmaz.
- Exception = yeniden dene (üstel backoff + jitter); `Outbox__MaxAttempts` sonunda satır `dead_lettered_at` ile işaretlenir ve bir daha otomatik denenmez.

## 6. Test

Gerçek PostgreSQL davranışı (kısıt, transaction, eşzamanlılık) `tests/SokagimdakiSorun.IntegrationTests` içinde, modülün kendi alt klasöründe test edilir:

```csharp
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class ComplaintPersistenceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ...()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(); // boş DB + platform migration'ı
        // modül bağlamını aynı bağlantı dizesiyle oluşturup MigrateAsync çağırın
    }
}
```

- `PostgresFixture` koleksiyon başına bir `postgres:18` konteyneri başlatır (Testcontainers); her test kendi boş veritabanını alır. SQLite/in-memory sağlayıcı kullanılmaz.
- Docker gerekir. Docker'sız çalıştırmak için: `dotnet test --solution SokagimdakiSorun.slnx --filter-not-trait "Category=Database"`.
- Örnek kullanım: `tests/SokagimdakiSorun.IntegrationTests/Database/` (test-only `SampleModuleDbContext`).
