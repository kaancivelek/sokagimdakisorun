using System.Globalization;
using SokagimdakiSorun.Complaints.Contracts;

namespace SokagimdakiSorun.Messaging.Domain;

/// <summary>Intake kuralları (yapılandırmadan).</summary>
/// <param name="Timeout">Hareketsizlik süresi; aşılınca oturum <see cref="IntakeState.Expired"/> olur.</param>
/// <param name="MaxPhotos">Bir başvuruya eklenebilecek azami fotoğraf.</param>
/// <param name="MaxInvalidInputs">Kategori/açıklama/konum adımında bu kadar geçersiz cevaptan sonra <see cref="IntakeState.NeedsHumanHelp"/>.</param>
internal sealed record IntakeRules(TimeSpan Timeout, int MaxPhotos, int MaxInvalidInputs)
{
    public int MaxDescriptionLength => IComplaintIntake.MaxDescriptionLength;

    public int MaxAddressLength => IComplaintIntake.MaxAddressLength;
}

/// <summary>Sağlayıcıdan bağımsız, normalize edilmiş gelen mesaj içeriği (intake girdisi).</summary>
/// <param name="Kind">Tür.</param>
/// <param name="Text">Metin (metin mesajı, etkileşimli cevap başlığı veya resim açıklaması).</param>
/// <param name="Latitude">Konum mesajında enlem.</param>
/// <param name="Longitude">Konum mesajında boylam.</param>
/// <param name="MediaFileId">Saklanan fotoğraf (Media).</param>
/// <param name="MediaRejected">Fotoğraf geldi ama Media doğrulamasından geçmedi.</param>
internal sealed record IntakeInput(
    MessageType Kind, string? Text, double? Latitude, double? Longitude, Guid? MediaFileId, bool MediaRejected);

/// <summary>Bir adımın sonucu: şikâyetçiye gidecek cevaplar ve şikâyet oluşturma talebi.</summary>
internal sealed record IntakeOutcome(IReadOnlyList<string> Replies, bool SubmitRequested)
{
    public static IntakeOutcome Reply(params string[] replies) => new(replies, false);

    public static readonly IntakeOutcome Submit = new([], true);
}

/// <summary>
/// Bir şikâyet için bilgilerin toplandığı geçici süreç ve durum makinesi (DOMAIN_MODEL §5). Geçişler yalnızca bu sınıfın
/// metotlarıyla olur; diyagramda olmayan geçiş yoktur. Şikâyet yalnızca <see cref="IntakeState.AwaitingConfirmation"/>
/// durumunda <b>açık onayla</b> istenir (<see cref="IntakeOutcome.SubmitRequested"/>); oluşturma çağrısını uygulama servisi yapar.
/// </summary>
internal sealed class IntakeSession
{
    private List<Guid> _categoryMenu = [];
    private List<Guid> _mediaFileIds = [];

    private IntakeSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    public IntakeState State { get; private set; }

    /// <summary>Seçilen kategori (Complaints; skaler).</summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>Son gösterilen kategori menüsü (numara → kimlik). Menü numaraları bu anlık görüntüye göre çözülür.</summary>
    public IReadOnlyList<Guid> CategoryMenu => _categoryMenu;

    public string? DescriptionDraft { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }

    public string? AddressText { get; private set; }

    /// <summary>Saklanan fotoğraflar (Media; <c>Temporary</c> — şikâyete bağlanınca kesinleşir).</summary>
    public IReadOnlyList<Guid> MediaFileIds => _mediaFileIds;

    /// <summary>Özetten "düzelt" ile dönüldüyse, alan doldurulunca doğrudan özete dönülür.</summary>
    public bool ReturnToConfirmation { get; private set; }

    /// <summary>Geçerli adımdaki ardışık geçersiz cevap sayısı.</summary>
    public int InvalidInputCount { get; private set; }

    /// <summary>Üretilen şikâyet (skaler; kısmi UNIQUE → intake başına tek şikâyet).</summary>
    public Guid? SubmittedComplaintId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>İyimser kilit (Npgsql <c>xmin</c>).</summary>
    public uint Version { get; private set; }

    public bool IsActive => State.IsActive();

    /// <summary>Yeni intake: <see cref="IntakeState.Started"/> → <see cref="IntakeState.AwaitingCategory"/> (menü gösterilir).</summary>
    public static (IntakeSession Session, IntakeOutcome Outcome) Start(
        Guid conversationId, IReadOnlyList<ComplaintCategorySummary> categories, string? preface, DateTimeOffset now, IntakeRules rules)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(rules);

        var session = new IntakeSession
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            State = IntakeState.Started,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + rules.Timeout,
        };

        session.MoveTo(IntakeState.AwaitingCategory, now);
        if (categories.Count == 0)
        {
            session.Complete(IntakeState.NeedsHumanHelp, now);
            return (session, WithPreface(preface, BotMessages.NoCategories));
        }

        session._categoryMenu = [.. categories.Select(c => c.Id)];
        return (session, WithPreface(preface, BotMessages.Welcome(categories)));
    }

    /// <summary>
    /// Gelen mesajla intake'i ilerletir. <paramref name="categories"/>: güncel etkin kategoriler (menü ve özet için).
    /// Aktif olmayan oturumda çağrılamaz.
    /// </summary>
    public IntakeOutcome Advance(IntakeInput input, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now, IntakeRules rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(rules);
        if (!IsActive)
        {
            throw new InvalidOperationException("Aktif olmayan intake ilerletilemez.");
        }

        ExpiresAt = now + rules.Timeout;
        UpdatedAt = now;

        var command = IntakeText.Normalize(input.Text);
        if (input.Kind == MessageType.Text && command is "iptal" or "vazgec")
        {
            Complete(IntakeState.Cancelled, now);
            return IntakeOutcome.Reply(BotMessages.Cancelled);
        }

        return State switch
        {
            IntakeState.AwaitingCategory => OnCategory(input, command, categories, now, rules),
            IntakeState.AwaitingDescription => OnDescription(input, categories, now, rules),
            IntakeState.AwaitingLocation => OnLocation(input, categories, now, rules),
            IntakeState.AwaitingMedia => OnMedia(input, command, categories, now, rules),
            IntakeState.AwaitingConfirmation => OnConfirmation(input, command, categories, now, rules),
            _ => throw new InvalidOperationException($"Beklenmeyen intake durumu: {State}."),
        };
    }

    /// <summary>Şikâyet oluşturuldu (onay sonrası). İdempotent: aynı şikâyetle tekrar çağrı bir şey değiştirmez.</summary>
    public void MarkSubmitted(Guid complaintId, DateTimeOffset now)
    {
        if (State == IntakeState.Submitted && SubmittedComplaintId == complaintId)
        {
            return;
        }

        if (State != IntakeState.AwaitingConfirmation)
        {
            throw new InvalidOperationException("Yalnızca onay bekleyen intake gönderilebilir.");
        }

        SubmittedComplaintId = complaintId;
        Complete(IntakeState.Submitted, now);
    }

    /// <summary>Gönderimde kategori geçersiz çıktı: <see cref="IntakeState.AwaitingCategory"/>'ye (düzeltme) döner.</summary>
    public IntakeOutcome ReturnToCategoryAfterRejectedSubmit(IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(categories);
        EnsureState(IntakeState.AwaitingConfirmation);
        CategoryId = null;
        if (categories.Count == 0)
        {
            MoveTo(IntakeState.AwaitingCategory, now);
            Complete(IntakeState.NeedsHumanHelp, now);
            return IntakeOutcome.Reply(BotMessages.NoCategories);
        }

        ReturnToConfirmation = true;
        _categoryMenu = [.. categories.Select(c => c.Id)];
        MoveTo(IntakeState.AwaitingCategory, now);
        return IntakeOutcome.Reply(BotMessages.CategoryNoLongerAvailable(categories));
    }

    /// <summary>Gönderimde ekler kullanılamaz çıktı (ör. süresi dolmuş geçici dosya): fotoğraflar çıkarılır, özet yeniden gösterilir.</summary>
    public IntakeOutcome DropMediaAfterRejectedSubmit(IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now)
    {
        EnsureState(IntakeState.AwaitingConfirmation);
        _mediaFileIds = [];
        UpdatedAt = now;
        return IntakeOutcome.Reply(BotMessages.AttachmentDropped, BotMessages.Summary(this, categories));
    }

    /// <summary>Hareketsizlik süresi dolduysa <see cref="IntakeState.Expired"/> yapar. Değiştiyse <see langword="true"/>.</summary>
    public bool ExpireIfDue(DateTimeOffset now)
    {
        if (!IsActive || ExpiresAt > now)
        {
            return false;
        }

        Complete(IntakeState.Expired, now);
        return true;
    }

    private IntakeOutcome OnCategory(
        IntakeInput input, string command, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now, IntakeRules rules)
    {
        var chosen = input.Kind == MessageType.Text ? ResolveCategory(command, categories) : null;
        if (chosen is null)
        {
            return Invalid(input, rules, now, () =>
            {
                var menu = CurrentMenu(categories);
                if (menu.Count == 0)
                {
                    Complete(IntakeState.NeedsHumanHelp, now);
                    return IntakeOutcome.Reply(BotMessages.NoCategories);
                }

                _categoryMenu = [.. menu.Select(c => c.Id)];
                return IntakeOutcome.Reply(BotMessages.ChooseCategoryAgain(menu));
            });
        }

        CategoryId = chosen.Id;
        return Next(IntakeState.AwaitingDescription, BotMessages.AskDescription(rules.MaxDescriptionLength), categories, now);
    }

    private IntakeOutcome OnDescription(IntakeInput input, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now, IntakeRules rules)
    {
        var text = input.Kind == MessageType.Text ? input.Text?.Trim() : null;
        if (string.IsNullOrEmpty(text) || text.Length > rules.MaxDescriptionLength)
        {
            return Invalid(input, rules, now, () => IntakeOutcome.Reply(BotMessages.DescriptionInvalid(rules.MaxDescriptionLength)));
        }

        DescriptionDraft = text;
        return Next(IntakeState.AwaitingLocation, BotMessages.AskLocation, categories, now);
    }

    private IntakeOutcome OnLocation(IntakeInput input, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now, IntakeRules rules)
    {
        if (input.Kind == MessageType.Location && input.Latitude is { } lat && input.Longitude is { } lon
            && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
        {
            Latitude = lat;
            Longitude = lon;
            AddressText = null;
        }
        else if (input.Kind == MessageType.Text && input.Text?.Trim() is { Length: > 0 } address && address.Length <= rules.MaxAddressLength)
        {
            Latitude = null;
            Longitude = null;
            AddressText = address;
        }
        else
        {
            return Invalid(input, rules, now, () => IntakeOutcome.Reply(BotMessages.LocationInvalid(rules.MaxAddressLength)));
        }

        return Next(IntakeState.AwaitingMedia, BotMessages.AskMedia, categories, now);
    }

    private IntakeOutcome OnMedia(
        IntakeInput input, string command, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now, IntakeRules rules)
    {
        switch (input.Kind)
        {
            case MessageType.Image when input.MediaFileId is { } media:
                AddPhoto(media, rules);
                return ToConfirmation(categories, now);
            case MessageType.Image:
                return IntakeOutcome.Reply(BotMessages.MediaRejected);
            case MessageType.Text when command is "atla" or "gec" or "yok" or "hayir":
                return ToConfirmation(categories, now);
            case MessageType.Unsupported:
                return IntakeOutcome.Reply(BotMessages.Unsupported, BotMessages.AskMedia);
            default:
                return IntakeOutcome.Reply(BotMessages.AskMedia);
        }
    }

    private IntakeOutcome OnConfirmation(
        IntakeInput input, string command, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now, IntakeRules rules)
    {
        if (input.Kind == MessageType.Image)
        {
            if (input.MediaFileId is not { } media)
            {
                return IntakeOutcome.Reply(BotMessages.MediaRejected, BotMessages.Summary(this, categories));
            }

            if (_mediaFileIds.Count >= rules.MaxPhotos)
            {
                return IntakeOutcome.Reply(BotMessages.TooManyPhotos(rules.MaxPhotos), BotMessages.Summary(this, categories));
            }

            AddPhoto(media, rules);
            return IntakeOutcome.Reply(BotMessages.Summary(this, categories));
        }

        if (input.Kind != MessageType.Text)
        {
            return input.Kind == MessageType.Unsupported
                ? IntakeOutcome.Reply(BotMessages.Unsupported, BotMessages.Summary(this, categories))
                : IntakeOutcome.Reply(BotMessages.Summary(this, categories));
        }

        switch (command)
        {
            case "1" or "evet" or "onay" or "onayla" or "onayliyorum":
                return IntakeOutcome.Submit;
            case "2":
                var menu = CurrentMenu(categories);
                _categoryMenu = [.. menu.Select(c => c.Id)];
                return Correct(IntakeState.AwaitingCategory, BotMessages.ChooseCategoryAgain(menu), now);
            case "3":
                return Correct(IntakeState.AwaitingDescription, BotMessages.AskDescription(rules.MaxDescriptionLength), now);
            case "4":
                return Correct(IntakeState.AwaitingLocation, BotMessages.AskLocation, now);
            default:
                return IntakeOutcome.Reply(BotMessages.Summary(this, categories));
        }
    }

    private IntakeOutcome Correct(IntakeState target, string prompt, DateTimeOffset now)
    {
        ReturnToConfirmation = true;
        MoveTo(target, now);
        return IntakeOutcome.Reply(prompt);
    }

    private IntakeOutcome Next(IntakeState next, string prompt, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now)
    {
        if (ReturnToConfirmation)
        {
            return ToConfirmation(categories, now);
        }

        MoveTo(next, now);
        return IntakeOutcome.Reply(prompt);
    }

    private IntakeOutcome ToConfirmation(IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now)
    {
        ReturnToConfirmation = false;
        MoveTo(IntakeState.AwaitingConfirmation, now);
        return IntakeOutcome.Reply(BotMessages.Summary(this, categories));
    }

    // Kategori/açıklama/konum adımında geçersiz cevap: sınır aşılınca NeedsHumanHelp (DOMAIN_MODEL §5 diyagramı).
    private IntakeOutcome Invalid(IntakeInput input, IntakeRules rules, DateTimeOffset now, Func<IntakeOutcome> reprompt)
    {
        InvalidInputCount++;
        if (InvalidInputCount >= rules.MaxInvalidInputs)
        {
            Complete(IntakeState.NeedsHumanHelp, now);
            return IntakeOutcome.Reply(BotMessages.NeedsHumanHelp);
        }

        var outcome = reprompt();
        return input.Kind == MessageType.Unsupported
            ? new IntakeOutcome([BotMessages.Unsupported, .. outcome.Replies], outcome.SubmitRequested)
            : outcome;
    }

    private void AddPhoto(Guid media, IntakeRules rules)
    {
        if (_mediaFileIds.Count < rules.MaxPhotos && !_mediaFileIds.Contains(media))
        {
            _mediaFileIds = [.. _mediaFileIds, media];
        }
    }

    private List<ComplaintCategorySummary> CurrentMenu(IReadOnlyList<ComplaintCategorySummary> categories) => [.. categories];

    private ComplaintCategorySummary? ResolveCategory(string command, IReadOnlyList<ComplaintCategorySummary> categories)
    {
        if (int.TryParse(command, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= 1 && number <= _categoryMenu.Count)
        {
            var id = _categoryMenu[number - 1];
            return categories.FirstOrDefault(c => c.Id == id);
        }

        return categories.FirstOrDefault(c =>
            IntakeText.Normalize(c.Name) == command || IntakeText.Normalize(c.Code) == command);
    }

    private void MoveTo(IntakeState next, DateTimeOffset now)
    {
        State = next;
        InvalidInputCount = 0;
        UpdatedAt = now;
    }

    private void Complete(IntakeState terminal, DateTimeOffset now)
    {
        State = terminal;
        CompletedAt = now;
        UpdatedAt = now;
        ReturnToConfirmation = false;
    }

    private void EnsureState(IntakeState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Intake {expected} durumunda değil ({State}).");
        }
    }

    private static IntakeOutcome WithPreface(string? preface, string message) =>
        preface is null ? IntakeOutcome.Reply(message) : IntakeOutcome.Reply(preface, message);
}

/// <summary>Komut karşılaştırması için metin normalizasyonu: kırp, küçük harf, Türkçe karakterleri ASCII'ye indir.</summary>
internal static class IntakeText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var chars = text.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                'ç' => 'c',
                'ğ' => 'g',
                'ı' or 'İ' => 'i', // InvariantGlobalization: 'İ' küçük harfe çevrilmeyebilir
                'Ç' => 'c',
                'Ğ' => 'g',
                'Ö' => 'o',
                'Ş' => 's',
                'Ü' => 'u',
                'ö' => 'o',
                'ş' => 's',
                'ü' => 'u',
                _ => chars[i],
            };
        }

        // "İ".ToLowerInvariant() → "i̇" (i + U+0307); birleşik noktayı at.
        return new string(chars).Replace("̇", string.Empty, StringComparison.Ordinal).TrimEnd('.', '!');
    }
}
