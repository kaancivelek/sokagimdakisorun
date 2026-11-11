using SokagimdakiSorun.Identity.Contracts;

namespace SokagimdakiSorun.Identity.Domain;

/// <summary>Bir kullanıcının dış kanal kimliği (ör. WhatsApp <c>wa_id</c>). UNIQUE(provider, external_id).</summary>
internal sealed class ExternalIdentity
{
    private ExternalIdentity()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public ExternalIdentityProvider Provider { get; private set; }

    public string ExternalId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static ExternalIdentity Create(Guid userId, ExternalIdentityProvider provider, string externalId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Provider = provider,
        ExternalId = externalId,
        CreatedAt = now,
    };
}
