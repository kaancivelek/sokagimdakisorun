using System.Text.Json.Serialization;
using SokagimdakiSorun.Api.ErrorHandling;
using SokagimdakiSorun.Api.Observability;
using SokagimdakiSorun.Api.OpenApi;
using SokagimdakiSorun.Api.Security;
using SokagimdakiSorun.Complaints;
using SokagimdakiSorun.Identity;
using SokagimdakiSorun.Media;
using SokagimdakiSorun.Messaging;
using SokagimdakiSorun.Platform;
using SokagimdakiSorun.Reporting;

// Composition root (docs/ARCHITECTURE.md §3). İş kuralı içermez; yalnızca altyapıyı kurar
// ve modüllerin kayıt metotlarını çağırır. Bu dosya build sırasında OpenAPI belgesi üretilirken
// de çalışır (GetDocument.Insider): burada dış kaynağa bağlanan kod koşulsuz çalıştırılmaz.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiProblemDetails();
builder.Services.AddApiAuthorization();
builder.Services.AddApiRateLimiting();
builder.Services.AddApiHealthChecks();
builder.Services.AddApiOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Platform modüllerden önce: PlatformDbContext, açılış migration'ı (Database__ApplyMigrationsOnStartup), OutboxDispatcher.
// Veritabanına kayıt sırasında değil, ilk kullanımda bağlanılır; build-zamanı OpenAPI üretimi veritabanı istemez.
builder.Services.AddPlatform(builder.Configuration);

builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddMediaModule(builder.Configuration);
builder.Services.AddComplaintsModule(builder.Configuration);
builder.Services.AddMessagingModule(builder.Configuration);
builder.Services.AddReportingModule(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapApiHealthEndpoints();
app.MapApiOpenApi(app.Environment);

app.MapIdentityEndpoints();
app.MapMediaEndpoints();
app.MapComplaintsEndpoints();
app.MapMessagingEndpoints();
app.MapReportingEndpoints();

await app.RunAsync();
