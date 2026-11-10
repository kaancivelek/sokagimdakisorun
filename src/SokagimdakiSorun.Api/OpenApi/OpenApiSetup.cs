using Microsoft.OpenApi;

namespace SokagimdakiSorun.Api.OpenApi;

/// <summary>
/// .NET 10 yerleşik OpenAPI üretimi (Microsoft.AspNetCore.OpenApi). Belge build sırasında
/// Microsoft.Extensions.ApiDescription.Server ile <c>docs/api/openapi.v1.json</c> dosyasına yazılır
/// (bkz. SokagimdakiSorun.Api.csproj ve docs/API_CONTRACT_STRATEGY.md §1).
/// </summary>
internal static class OpenApiSetup
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddApiOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Sokağımdaki Sorun API",
                Version = DocumentName,
            };

            // Sunucu adresi isteğe/ortama göre değişir; anlık görüntünün kararlı kalması için yazılmaz.
            document.Servers = [];

            return Task.CompletedTask;
        }));

        return services;
    }

    /// <summary>
    /// Belgeyi yalnızca geliştirme ortamında <c>/openapi/v1.json</c> adresinden sunar.
    /// Üretimde sözleşme depodaki anlık görüntüdür; uç nokta açılmaz.
    /// </summary>
    public static IEndpointRouteBuilder MapApiOpenApi(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            endpoints.MapOpenApi().AllowAnonymous();
        }

        return endpoints;
    }
}
