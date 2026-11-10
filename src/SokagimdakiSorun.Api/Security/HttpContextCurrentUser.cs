using System.Security.Claims;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Api.Security;

/// <summary>
/// <see cref="ICurrentUser"/>'ın HTTP isteği üzerinden uygulaması. Kullanıcı kimliği
/// <see cref="ClaimTypes.NameIdentifier"/> claim'inden GUID olarak okunur; claim adları WS-04'te
/// kesinleşirse burası güncellenir.
/// </summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public bool IsInRole(string role) => IsAuthenticated && Principal!.IsInRole(role);
}
