using System.Reflection;

namespace SokagimdakiSorun.Platform.Hosting;

/// <summary>
/// Uygulama gerçek bir sunucu olarak değil, build-zamanı aracı tarafından çalıştırılıyor mu?
/// <c>GetDocument.Insider</c>: build sırasında OpenAPI belgesi üretimi (yol haritası V-1 notu).
/// <c>ef</c>: <c>dotnet ef</c>. Bu durumlarda veritabanına bağlanan açılış işleri çalışmaz.
/// </summary>
internal static class BuildTimeTooling
{
    public static bool IsRunning { get; } =
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider" or "ef";
}
