namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>
/// <c>Database</c> yapılandırma bölümü (ARCHITECTURE §9). Bağlantı dizesi ayrıca
/// <c>ConnectionStrings:Default</c> (ortam değişkeni <c>ConnectionStrings__Default</c>) anahtarından okunur.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Bağlantı dizesinin <c>ConnectionStrings</c> altındaki adı.</summary>
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// <c>true</c> ise kayıtlı tüm modül bağlamlarının migration'ları açılışta uygulanır. Yalnızca yerel
    /// geliştirme içindir; varsayılan <c>false</c>. Üretim yöntemi deployment iş akışında (WS-11) belirlenir.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}
