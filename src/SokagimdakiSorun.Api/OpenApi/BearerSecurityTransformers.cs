using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SokagimdakiSorun.Api.OpenApi;

/// <summary>
/// Her uç noktanın güvenlik gereksinimini OpenAPI belgesine yazar (docs/API_CONTRACT_STRATEGY.md §6). Varsayılan-reddet
/// politikası gereği <c>AllowAnonymous</c> olmayan her işlem <c>Bearer</c> şeması ister. OpenAPI 3.1'de bearer
/// gereksinimindeki liste rol adlarını taşıyabilir; uç noktanın politikalarındaki roller buraya yazılır
/// (boş liste: kimliği doğrulanmış herhangi bir kullanıcı). Kaynak kapsamı (ör. şikâyet erişimi) belgelenmez; uç nokta açıklamasındadır.
/// </summary>
internal static class BearerSecurityTransformers
{
    public const string SchemeName = "Bearer";

    public static Task AddSchemeAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "ASP.NET Core Identity'nin opak erişim token'ı (POST /api/v1/auth/login). Yalnızca panel sunucusu (BFF) gönderir.",
        };
        return Task.CompletedTask;
    }

    public static async Task AddRequirementAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return;
        }

        var policyProvider = context.ApplicationServices.GetRequiredService<IAuthorizationPolicyProvider>();
        var roles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var authorizeData in metadata.OfType<IAuthorizeData>())
        {
            AddRoles(roles, authorizeData.Roles?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (authorizeData.Policy is { } policyName && await policyProvider.GetPolicyAsync(policyName) is { } policy)
            {
                AddRoles(roles, policy);
            }
        }

        foreach (var policy in metadata.OfType<AuthorizationPolicy>())
        {
            AddRoles(roles, policy);
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [.. roles],
        });
    }

    private static void AddRoles(SortedSet<string> roles, AuthorizationPolicy policy)
    {
        foreach (var requirement in policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>())
        {
            AddRoles(roles, requirement.AllowedRoles);
        }
    }

    private static void AddRoles(SortedSet<string> roles, IEnumerable<string>? values)
    {
        foreach (var role in values ?? [])
        {
            roles.Add(role);
        }
    }
}
