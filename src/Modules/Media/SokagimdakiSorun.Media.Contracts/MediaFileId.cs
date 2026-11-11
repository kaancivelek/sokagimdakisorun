namespace SokagimdakiSorun.Media.Contracts;

/// <summary>
/// Saklanan bir dosyanın opak kimliği (ADR-0004 §2, DOMAIN_MODEL I-10). Diğer modüller dosyayı yalnızca bununla tanır;
/// storage key veya fiziksel yol hiçbir sözleşmede bulunmaz.
/// </summary>
public readonly record struct MediaFileId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
