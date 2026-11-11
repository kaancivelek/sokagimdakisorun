using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Identity.Contracts;

namespace SokagimdakiSorun.Complaints.Infrastructure;

/// <summary><c>Complaints:DemoSeed</c> bölümü (ortam değişkeni <c>Complaints__DemoSeed__Enabled</c>).</summary>
internal sealed class DemoSeedOptions
{
    public const string SectionName = "Complaints:DemoSeed";

    /// <summary>Varsayılan <see langword="false"/>. Yalnızca <c>Development</c> ortamında etkilidir.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Pilot bölge (Çanakkale / Gelibolu / Yazıcızade) için <b>tamamen temsili</b> tohum verisi (PROJECT_BRIEF §10, OD-6).
/// Kurallar: gerçek kurum/kişi/iletişim bilgisi yok; birim adları "(Temsili)" ile işaretli; koordinatlar mahalle çevresinde
/// yaklaşık ve temsili; şikâyetçiler <c>demo-reporter-NN</c> sahte dış kimlikleriyle (telefon numarası değil).
/// Yalnızca <c>Development</c> ortamında <b>ve</b> <c>Complaints__DemoSeed__Enabled=true</c> iken, migration'lardan sonra çalışır.
/// Idempotenttir: kod ve intake kimlikleri sabittir; ikinci çalıştırma yeni kayıt üretmez.
/// </summary>
internal sealed partial class DemoDataSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<DemoSeedOptions> options,
    IHostEnvironment environment,
    ILogger<DemoDataSeeder> logger) : IHostedService
{
    private static readonly bool IsBuildTimeTooling =
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider" or "ef";

    internal static readonly (string Code, string Name, string Description, int Sort)[] Categories =
    [
        ("yol-kaldirim", "Yol ve kaldırım", "Çukur, asfalt hasarı, bozuk kaldırım", 10),
        ("cop-atik", "Çöp ve atık", "Çöp birikmesi, kaçak döküm, konteyner hasarı", 20),
        ("aydinlatma", "Sokak aydınlatması", "Çalışmayan lamba, hasarlı direk", 30),
        ("park-yesil-alan", "Park ve yeşil alan", "Kırık bank, sulama, ağaç hasarı", 40),
        ("trafik-isaret", "Trafik işareti ve yaya geçidi", "Hasarlı tabela, silinmiş yaya geçidi", 50),
        ("erisilebilirlik", "Engelli erişimi", "Rampa engeli, erişilemeyen geçiş", 60),
    ];

    internal static readonly (string Code, string Name)[] Units =
    [
        ("temsili-fen-isleri", "Fen İşleri Birimi (Temsili)"),
        ("temsili-temizlik", "Temizlik İşleri Birimi (Temsili)"),
        ("temsili-park-bahceler", "Park ve Bahçeler Birimi (Temsili)"),
        ("temsili-aydinlatma", "Aydınlatma Hizmet Birimi (Temsili)"),
        ("temsili-ulasim", "Ulaşım Hizmetleri Birimi (Temsili)"),
    ];

    internal static readonly (string Category, string Unit)[] Rules =
    [
        ("yol-kaldirim", "temsili-fen-isleri"),
        ("cop-atik", "temsili-temizlik"),
        ("aydinlatma", "temsili-aydinlatma"),
        ("park-yesil-alan", "temsili-park-bahceler"),
        ("trafik-isaret", "temsili-ulasim"),
        ("erisilebilirlik", "temsili-fen-isleri"),
    ];

    // Yaklaşık, temsili koordinatlar (Gelibolu merkez çevresi); gerçek bir adresi işaret etmez.
    internal static readonly (Guid IntakeId, string Reporter, string Category, string Description, double? Lat, double? Lon, string? Address)[] SampleComplaints =
    [
        (new("0193a000-0000-7000-8000-000000000001"), "demo-reporter-01", "yol-kaldirim",
            "[Örnek veri] Mahalle içindeki ara sokakta derin bir çukur var, araçlar zorlanıyor.", 40.4105, 26.6712, null),
        (new("0193a000-0000-7000-8000-000000000002"), "demo-reporter-02", "cop-atik",
            "[Örnek veri] Konteyner iki gündür boşaltılmadı, çevreye taşmış.", 40.4098, 26.6698, null),
        (new("0193a000-0000-7000-8000-000000000003"), "demo-reporter-01", "aydinlatma",
            "[Örnek veri] Sokak lambası akşamları yanmıyor.", null, null, "Yazıcızade Mahallesi, Gelibolu (temsili konum)"),
        (new("0193a000-0000-7000-8000-000000000004"), "demo-reporter-03", "park-yesil-alan",
            "[Örnek veri] Parktaki bankın oturma kısmı kırık.", 40.4112, 26.6721, null),
        (new("0193a000-0000-7000-8000-000000000005"), "demo-reporter-02", "erisilebilirlik",
            "[Örnek veri] Kaldırım rampasının önüne sürekli araç park ediliyor.", 40.4101, 26.6705, null),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsBuildTimeTooling || !options.Value.Enabled || !environment.IsDevelopment())
        {
            return;
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<ComplaintsDbContext>();
            await SeedCatalogAsync(db, cancellationToken);

            var reporters = scope.ServiceProvider.GetRequiredService<IReporterRegistry>();
            var intake = scope.ServiceProvider.GetRequiredService<IComplaintIntake>();
            var categoryIds = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.Id, cancellationToken);
            var created = 0;
            foreach (var sample in SampleComplaints)
            {
                var reporter = await reporters.FindOrCreateAsync(
                    ExternalIdentityProvider.WhatsApp, sample.Reporter, "Örnek Vatandaş", cancellationToken);
                if (reporter.IsFailure)
                {
                    throw new InvalidOperationException("Demo şikâyetçisi oluşturulamadı: " + reporter.Error.Code);
                }

                var result = await intake.SubmitFromIntakeAsync(new SubmitFromIntakeRequest(
                    sample.IntakeId, reporter.Value.UserId, categoryIds[sample.Category], sample.Description,
                    sample.Lat, sample.Lon, sample.Address,
                    sample.Lat is null ? LocationSource.TextAddress : LocationSource.WhatsAppPin, []), cancellationToken);
                if (result.IsFailure)
                {
                    throw new InvalidOperationException("Demo şikâyeti oluşturulamadı: " + result.Error.Code);
                }

                created += result.Value.Created ? 1 : 0;
            }

            LogSeeded(logger, created);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedCatalogAsync(ComplaintsDbContext db, CancellationToken ct)
    {
        var existingCategories = await db.Categories.Select(c => c.Code).ToListAsync(ct);
        foreach (var (code, name, description, sort) in Categories.Where(c => !existingCategories.Contains(c.Code)))
        {
            db.Categories.Add(ComplaintCategory.Create(code, name, description, sort, isActive: true));
        }

        var existingUnits = await db.ServiceUnits.Select(u => u.Code).ToListAsync(ct);
        foreach (var (code, name) in Units.Where(u => !existingUnits.Contains(u.Code)))
        {
            db.ServiceUnits.Add(ServiceUnit.Create(code, name, isActive: true));
        }

        await db.SaveChangesAsync(ct);

        if (!await db.RoutingRules.AnyAsync(ct))
        {
            var categoryIds = await db.Categories.ToDictionaryAsync(c => c.Code, c => c.Id, ct);
            var unitIds = await db.ServiceUnits.ToDictionaryAsync(u => u.Code, u => u.Id, ct);
            db.RoutingRules.AddRange(Rules.Select(r => RoutingRule.Create(categoryIds[r.Category], unitIds[r.Unit], priority: 0, isActive: true)));
            await db.SaveChangesAsync(ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Temsili demo verisi yüklendi; yeni şikâyet sayısı {Created}.")]
    private static partial void LogSeeded(ILogger logger, int created);
}
