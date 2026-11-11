namespace SokagimdakiSorun.Media.Domain;

/// <summary>Dosya yaşam döngüsü (ADR-0004 §4): <c>Temporary → Committed → Deleted</c>; <c>Temporary → Deleted</c> (temizlik).</summary>
internal enum MediaFileStatus
{
    Temporary,
    Committed,
    Deleted,
}
