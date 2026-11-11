using System.Security.Cryptography;

namespace SokagimdakiSorun.Complaints.Domain;

/// <summary>Şikâyet kategorisi (MVP: düz liste).</summary>
internal sealed class ComplaintCategory
{
    public const int MaxCodeLength = 64;
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 500;

    /// <summary>Kod biçimi: küçük harf, rakam ve tire (DB CHECK ile aynı).</summary>
    public const string CodePattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    private ComplaintCategory()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    public int SortOrder { get; private set; }

    public static ComplaintCategory Create(string code, string name, string? description, int sortOrder, bool isActive, Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        Code = code,
        Name = name,
        Description = description,
        SortOrder = sortOrder,
        IsActive = isActive,
    };

    public void Update(string? name, string? description, bool clearDescription, int? sortOrder, bool? isActive)
    {
        Name = name ?? Name;
        Description = clearDescription ? null : description ?? Description;
        SortOrder = sortOrder ?? SortOrder;
        IsActive = isActive ?? IsActive;
    }
}

/// <summary>Hizmet birimi (temsili). Üyelik moderatörün yetki kapsamını belirler.</summary>
internal sealed class ServiceUnit
{
    public const int MaxCodeLength = 64;
    public const int MaxNameLength = 128;

    private readonly List<ServiceUnitMember> _members = [];

    private ServiceUnit()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<ServiceUnitMember> Members => _members;

    public static ServiceUnit Create(string code, string name, bool isActive, Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        Code = code,
        Name = name,
        IsActive = isActive,
    };

    public void Update(string? name, bool? isActive)
    {
        Name = name ?? Name;
        IsActive = isActive ?? IsActive;
    }
}

/// <summary>Hizmet birimi üyeliği; PK(service_unit_id, user_id). <c>user_id</c> Identity'ye skaler başvurudur.</summary>
internal sealed class ServiceUnitMember
{
    private ServiceUnitMember()
    {
    }

    public Guid ServiceUnitId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public static ServiceUnitMember Create(Guid serviceUnitId, Guid userId, DateTimeOffset now) => new()
    {
        ServiceUnitId = serviceUnitId,
        UserId = userId,
        AddedAt = now,
    };
}

/// <summary>Yönlendirme kuralı: kategori → önerilen hizmet birimi. Yalnızca öneri üretir; atamayı moderatör yapar.</summary>
internal sealed class RoutingRule
{
    public const int MaxDistrictLength = 128;

    private RoutingRule()
    {
    }

    public Guid Id { get; private set; }

    public Guid CategoryId { get; private set; }

    public Guid ServiceUnitId { get; private set; }

    /// <summary>MVP'de kullanılmaz (her zaman <see langword="null"/>).</summary>
    public string? District { get; private set; }

    /// <summary>Küçük değer önce gelir.</summary>
    public int Priority { get; private set; }

    public bool IsActive { get; private set; }

    public static RoutingRule Create(Guid categoryId, Guid serviceUnitId, int priority, bool isActive) => new()
    {
        Id = Guid.CreateVersion7(),
        CategoryId = categoryId,
        ServiceUnitId = serviceUnitId,
        Priority = priority,
        IsActive = isActive,
    };
}

/// <summary>
/// <c>SS-XXXX-XXXX</c> biçiminde rastgele, ardışık olmayan referans. Karıştırılması zor karakterler (0/O, 1/I/L) yoktur:
/// 31 karakterlik alfabe, 8 konum → ~8,5·10¹¹ olasılık. Benzersizlik UNIQUE kısıtıyla garanti edilir; çakışmada yeniden üretilir.
/// </summary>
internal static class PublicReferenceGenerator
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>DB CHECK ile aynı desen.</summary>
    public const string Pattern = "^SS-[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}$";

    public static string Next()
    {
        Span<char> chars = stackalloc char[8];
        RandomNumberGenerator.GetItems(Alphabet.AsSpan(), chars);
        return string.Concat("SS-", chars[..4], "-", chars[4..]);
    }
}
