namespace SokagimdakiSorun.SharedKernel;

/// <summary>
/// Değer taşımayan bir işlemin sonucu. Beklenen iş hataları exception yerine
/// bu tiple taşınır (docs/ARCHITECTURE.md §11).
/// </summary>
public class Result
{
    private readonly Error? _error;

    /// <summary>Sonuç oluşturur; başarı ile hata birbirini dışlar.</summary>
    protected Result(Error? error)
    {
        _error = error;
    }

    /// <summary>İşlem başarılı mı.</summary>
    public bool IsSuccess => _error is null;

    /// <summary>İşlem başarısız mı.</summary>
    public bool IsFailure => _error is not null;

    /// <summary>Hata. Başarılı sonuçta erişilirse <see cref="InvalidOperationException"/> fırlatır.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("Başarılı sonucun hatası yoktur.");

    /// <summary>Başarılı sonuç.</summary>
    public static Result Success() => new(null);

    /// <summary>Başarısız sonuç.</summary>
    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    /// <summary>Değer taşıyan başarılı sonuç.</summary>
    public static Result<TValue> Success<TValue>(TValue value) => Result<TValue>.Success(value);

    /// <summary>Değer tipli başarısız sonuç.</summary>
    public static Result<TValue> Failure<TValue>(Error error) => Result<TValue>.Failure(error);

    /// <summary>Hatayı başarısız sonuca dönüştürür.</summary>
    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>Değer taşıyan bir işlemin sonucu.</summary>
/// <typeparam name="TValue">Başarı durumundaki değerin tipi.</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue? value, Error? error)
        : base(error)
    {
        _value = value;
    }

    /// <summary>Değer. Başarısız sonuçta erişilirse <see cref="InvalidOperationException"/> fırlatır.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Başarısız sonucun değeri yoktur.");

    /// <summary>Başarılı sonuç.</summary>
    public static Result<TValue> Success(TValue value) => new(value, null);

    /// <summary>Başarısız sonuç.</summary>
    public static new Result<TValue> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TValue>(default, error);
    }

    /// <summary>Değeri başarılı sonuca dönüştürür.</summary>
    public static implicit operator Result<TValue>(TValue value) => Success(value);

    /// <summary>Hatayı başarısız sonuca dönüştürür.</summary>
    public static implicit operator Result<TValue>(Error error) => Failure(error);
}
