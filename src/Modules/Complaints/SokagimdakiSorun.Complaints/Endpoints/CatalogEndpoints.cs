using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Application;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Endpoints;

/// <summary>
/// Kategori, hizmet birimi (+ üyelik) ve yönlendirme kuralı yönetimi (API_CONTRACT_STRATEGY §8). Okuma panel kullanıcısına,
/// yazma yalnızca Admin'e açıktır. Kayıtlar silinmez; <c>isActive</c> ile devre dışı bırakılır (şikâyetlerin FK'si korunur).
/// </summary>
internal static partial class CatalogEndpoints
{
    private static readonly Error CategoryNotFound = Error.NotFound("category.not_found", "Kategori bulunamadı.");
    private static readonly Error CategoryCodeTaken = Error.Conflict("category.code_taken", "Bu kodla bir kategori zaten var.");
    private static readonly Error UnitNotFound = Error.NotFound("service_unit.not_found", "Hizmet birimi bulunamadı.");
    private static readonly Error UnitCodeTaken = Error.Conflict("service_unit.code_taken", "Bu kodla bir hizmet birimi zaten var.");
    private static readonly Error MemberInvalid = Error.BusinessRule("service_unit.member_invalid", "Üye yalnızca etkin bir panel kullanıcısı olabilir.");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var categories = endpoints.MapGroup("/api/v1/categories").WithTags("Categories")
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);

        categories.MapGet("/", ListCategoriesAsync).WithName("ListCategories")
            .WithSummary("Tüm kategoriler (etkin olmayanlar dâhil), sort_order'a göre.")
            .RequireAuthorization(IdentityPolicies.Staff)
            .Produces<IReadOnlyList<CategoryResponse>>();
        categories.MapPost("/", CreateCategoryAsync).WithName("CreateCategory").WithSummary("Kategori oluşturur.")
            .RequireAuthorization(IdentityPolicies.Admin)
            .Produces<CategoryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);
        categories.MapPatch("/{id:guid}", UpdateCategoryAsync).WithName("UpdateCategory")
            .WithSummary("Ad, açıklama (boş metin temizler), sıra veya etkinlik günceller. Kod değişmez.")
            .RequireAuthorization(IdentityPolicies.Admin)
            .Produces<CategoryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        var units = endpoints.MapGroup("/api/v1/service-units").WithTags("ServiceUnits")
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);

        units.MapGet("/", ListUnitsAsync).WithName("ListServiceUnits")
            .WithSummary("Hizmet birimleri ve üyeleri (temsili birimler).")
            .RequireAuthorization(IdentityPolicies.Staff)
            .Produces<IReadOnlyList<ServiceUnitResponse>>();
        units.MapPost("/", CreateUnitAsync).WithName("CreateServiceUnit").WithSummary("Hizmet birimi oluşturur.")
            .RequireAuthorization(IdentityPolicies.Admin)
            .Produces<ServiceUnitResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);
        units.MapPatch("/{id:guid}", UpdateUnitAsync).WithName("UpdateServiceUnit").WithSummary("Ad veya etkinlik günceller. Kod değişmez.")
            .RequireAuthorization(IdentityPolicies.Admin)
            .Produces<ServiceUnitResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        units.MapPost("/{id:guid}/members", AddMemberAsync).WithName("AddServiceUnitMember")
            .WithSummary("Birime üye (etkin panel kullanıcısı) ekler; idempotent. Üyelik moderatörün kapsamını belirler.")
            .RequireAuthorization(IdentityPolicies.Admin)
            .Produces<ServiceUnitResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        units.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync).WithName("RemoveServiceUnitMember")
            .WithSummary("Birimden üyeyi çıkarır; idempotent.")
            .RequireAuthorization(IdentityPolicies.Admin)
            .Produces<ServiceUnitResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var rules = endpoints.MapGroup("/api/v1/routing-rules").WithTags("RoutingRules")
            .RequireAuthorization(IdentityPolicies.Admin)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);

        rules.MapGet("/", GetRulesAsync).WithName("GetRoutingRules")
            .WithSummary("Yönlendirme kuralları (kategori → önerilen birim). Yalnızca öneri üretir.")
            .Produces<RoutingRuleSet>();
        rules.MapPut("/", ReplaceRulesAsync).WithName("ReplaceRoutingRules")
            .WithSummary("Kural kümesinin tamamını değiştirir. Kategori ve birim mevcut olmalı; (kategori, birim) çifti tekrarlanamaz.")
            .Produces<RoutingRuleSet>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    // ---- Kategoriler ----

    private static async Task<IResult> ListCategoriesAsync(ComplaintsDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Code)
            .Select(c => new CategoryResponse(c.Id, c.Code, c.Name, c.Description, c.IsActive, c.SortOrder)).ToListAsync(ct));

    private static async Task<IResult> CreateCategoryAsync(CreateCategoryRequest request, ComplaintsDbContext db, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > ComplaintCategory.MaxCodeLength || !CodeRegex().IsMatch(code))
        {
            return ProblemResults.Validation("code", "Kod zorunludur; küçük harf, rakam ve tire içerebilir (ör. yol-kaldirim).");
        }

        if (ValidateName(request.Name, ComplaintCategory.MaxNameLength, out var name) is { } nameError)
        {
            return nameError;
        }

        if (request.Description?.Trim().Length > ComplaintCategory.MaxDescriptionLength)
        {
            return ProblemResults.Validation("description", $"Açıklama en fazla {ComplaintCategory.MaxDescriptionLength} karakter olabilir.");
        }

        var category = ComplaintCategory.Create(code, name, NullIfBlank(request.Description), request.SortOrder ?? 0, request.IsActive ?? true);
        db.Categories.Add(category);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ComplaintPersistence.IsUniqueViolation(ex, ComplaintsDbContext.CategoryCodeUniqueIndex))
        {
            return ProblemResults.From(CategoryCodeTaken);
        }

        return TypedResults.Created($"/api/v1/categories/{category.Id}", ToResponse(category));
    }

    private static async Task<IResult> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, ComplaintsDbContext db, CancellationToken ct)
    {
        string? name = null;
        if (request.Name is not null && ValidateName(request.Name, ComplaintCategory.MaxNameLength, out name) is { } nameError)
        {
            return nameError;
        }

        if (request.Description?.Trim().Length > ComplaintCategory.MaxDescriptionLength)
        {
            return ProblemResults.Validation("description", $"Açıklama en fazla {ComplaintCategory.MaxDescriptionLength} karakter olabilir.");
        }

        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
        {
            return ProblemResults.From(CategoryNotFound);
        }

        category.Update(name, NullIfBlank(request.Description), request.Description is not null && NullIfBlank(request.Description) is null,
            request.SortOrder, request.IsActive);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToResponse(category));
    }

    // ---- Hizmet birimleri ----

    private static async Task<IResult> ListUnitsAsync(ComplaintsDbContext db, ComplaintQueries queries, CancellationToken ct)
    {
        var units = await db.ServiceUnits.AsNoTracking().Include(u => u.Members).OrderBy(u => u.Code).ToListAsync(ct);
        var names = await queries.LoadUsersAsync(units.SelectMany(u => u.Members).Select(m => m.UserId), ct);
        return TypedResults.Ok(units.Select(u => ToResponse(u, names)).ToList());
    }

    private static async Task<IResult> CreateUnitAsync(CreateServiceUnitRequest request, ComplaintsDbContext db, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > ServiceUnit.MaxCodeLength || !CodeRegex().IsMatch(code))
        {
            return ProblemResults.Validation("code", "Kod zorunludur; küçük harf, rakam ve tire içerebilir (ör. fen-isleri).");
        }

        if (ValidateName(request.Name, ServiceUnit.MaxNameLength, out var name) is { } nameError)
        {
            return nameError;
        }

        var unit = ServiceUnit.Create(code, name, request.IsActive ?? true);
        db.ServiceUnits.Add(unit);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ComplaintPersistence.IsUniqueViolation(ex, ComplaintsDbContext.ServiceUnitCodeUniqueIndex))
        {
            return ProblemResults.From(UnitCodeTaken);
        }

        return TypedResults.Created($"/api/v1/service-units/{unit.Id}", ToResponse(unit, new Dictionary<Guid, UserSummary>()));
    }

    private static async Task<IResult> UpdateUnitAsync(Guid id, UpdateServiceUnitRequest request, ComplaintsDbContext db, ComplaintQueries queries, CancellationToken ct)
    {
        string? name = null;
        if (request.Name is not null && ValidateName(request.Name, ServiceUnit.MaxNameLength, out name) is { } nameError)
        {
            return nameError;
        }

        var unit = await db.ServiceUnits.Include(u => u.Members).SingleOrDefaultAsync(u => u.Id == id, ct);
        if (unit is null)
        {
            return ProblemResults.From(UnitNotFound);
        }

        unit.Update(name, request.IsActive);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToResponse(unit, await queries.LoadUsersAsync(unit.Members.Select(m => m.UserId), ct)));
    }

    private static async Task<IResult> AddMemberAsync(
        Guid id, AddMemberRequest request, ComplaintsDbContext db, IUserDirectory users, ComplaintQueries queries, TimeProvider time, CancellationToken ct)
    {
        if (request.UserId is not { } userId)
        {
            return ProblemResults.Validation("userId", "Kullanıcı zorunludur.");
        }

        if (!await db.ServiceUnits.AnyAsync(u => u.Id == id, ct))
        {
            return ProblemResults.From(UnitNotFound);
        }

        if (await users.GetUserAsync(userId, ct) is not { Kind: UserKind.Staff, Status: UserStatus.Active })
        {
            return ProblemResults.From(MemberInvalid);
        }

        if (!await db.ServiceUnitMembers.AnyAsync(m => m.ServiceUnitId == id && m.UserId == userId, ct))
        {
            db.ServiceUnitMembers.Add(ServiceUnitMember.Create(id, userId, time.GetUtcNow()));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // eşzamanlı aynı ekleme: sonuç aynı
            }
        }

        return await UnitResponseAsync(id, db, queries, ct);
    }

    private static async Task<IResult> RemoveMemberAsync(Guid id, Guid userId, ComplaintsDbContext db, ComplaintQueries queries, CancellationToken ct)
    {
        if (!await db.ServiceUnits.AnyAsync(u => u.Id == id, ct))
        {
            return ProblemResults.From(UnitNotFound);
        }

        await db.ServiceUnitMembers.Where(m => m.ServiceUnitId == id && m.UserId == userId).ExecuteDeleteAsync(ct);
        return await UnitResponseAsync(id, db, queries, ct);
    }

    // ---- Yönlendirme kuralları ----

    private static async Task<IResult> GetRulesAsync(ComplaintsDbContext db, CancellationToken ct) =>
        TypedResults.Ok(new RoutingRuleSet(await db.RoutingRules.AsNoTracking()
            .OrderBy(r => r.CategoryId).ThenBy(r => r.Priority).ThenBy(r => r.ServiceUnitId)
            .Select(r => new RoutingRuleItem(r.CategoryId, r.ServiceUnitId, r.Priority, r.IsActive)).ToListAsync(ct)));

    private static async Task<IResult> ReplaceRulesAsync(RoutingRuleSet request, ComplaintsDbContext db, CancellationToken ct)
    {
        var items = request.Rules ?? [];
        if (items.Count > 500)
        {
            return ProblemResults.Validation("rules", "En fazla 500 kural tanımlanabilir.");
        }

        if (items.GroupBy(r => (r.CategoryId, r.ServiceUnitId)).Any(g => g.Count() > 1))
        {
            return ProblemResults.Validation("rules", "Aynı (kategori, birim) çifti birden fazla kez verilemez.");
        }

        var categoryIds = items.Select(r => r.CategoryId).Distinct().ToArray();
        var unitIds = items.Select(r => r.ServiceUnitId).Distinct().ToArray();
        var knownCategories = await db.Categories.CountAsync(c => categoryIds.Contains(c.Id), ct);
        var knownUnits = await db.ServiceUnits.CountAsync(u => unitIds.Contains(u.Id), ct);
        if (knownCategories != categoryIds.Length || knownUnits != unitIds.Length)
        {
            return ProblemResults.Validation("rules", "Kurallardaki kategori ve birimler mevcut olmalıdır.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var transaction = await db.Database.BeginTransactionAsync(ct);
            await using (transaction.ConfigureAwait(false))
            {
                await db.RoutingRules.ExecuteDeleteAsync(ct);
                db.RoutingRules.AddRange(items.Select(r => RoutingRule.Create(r.CategoryId, r.ServiceUnitId, r.Priority, r.IsActive)));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
        });

        return await GetRulesAsync(db, ct);
    }

    // ---- Yardımcılar ----

    private static async Task<IResult> UnitResponseAsync(Guid id, ComplaintsDbContext db, ComplaintQueries queries, CancellationToken ct)
    {
        var unit = await db.ServiceUnits.AsNoTracking().Include(u => u.Members).SingleAsync(u => u.Id == id, ct);
        return TypedResults.Ok(ToResponse(unit, await queries.LoadUsersAsync(unit.Members.Select(m => m.UserId), ct)));
    }

    private static IResult? ValidateName(string? input, int maxLength, out string name)
    {
        name = input?.Trim() ?? string.Empty;
        return name.Length == 0 || name.Length > maxLength
            ? ProblemResults.Validation("name", $"Ad zorunludur ve en fazla {maxLength} karakter olabilir.")
            : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CategoryResponse ToResponse(ComplaintCategory c) => new(c.Id, c.Code, c.Name, c.Description, c.IsActive, c.SortOrder);

    private static ServiceUnitResponse ToResponse(ServiceUnit u, IReadOnlyDictionary<Guid, UserSummary> names) => new(
        u.Id, u.Code, u.Name, u.IsActive,
        [.. u.Members.OrderBy(m => m.AddedAt).Select(m => ComplaintQueries.ToRef(names, m.UserId)!)]);

    [GeneratedRegex(ComplaintCategory.CodePattern)]
    private static partial Regex CodeRegex();
}
