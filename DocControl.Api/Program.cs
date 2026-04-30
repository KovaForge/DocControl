using DocControl.Api.Infrastructure;
using DocControl.Api.Services;
using DocControl.Infrastructure.Data;
using DocControl.Infrastructure.Services;
using DocControl.AI;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.Configure<JsonSerializerOptions>(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var connStr = config.GetConnectionString("Db")
                 ?? config["DbConnection"];
    if (string.IsNullOrWhiteSpace(connStr))
    {
        // Allow host to start even if DB is not configured; operations will fail when used.
        // This helps deployments complete and lets us inspect runtime logs.
        connStr = "Host=127.0.0.1;Port=5432;Username=placeholder;Password=placeholder;Database=placeholder";
    }
    var sanitized = ConnectionStringHelper.Sanitize(connStr);
    return new DbConnectionFactory(sanitized);
});
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddHostedService<DatabaseMigratorHostedService>();

builder.Services.AddSingleton<ConfigRepository>();
builder.Services.AddSingleton<ConfigService>();
// Legacy key storage/encryption removed; keys are encrypted with user password-derived keys.

builder.Services.AddSingleton<ProjectRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<UserAuthRepository>();
builder.Services.AddSingleton<ProjectMemberRepository>();
builder.Services.AddSingleton<ProjectInviteRepository>();
builder.Services.AddSingleton<AuthContextFactory>();
builder.Services.AddSingleton<AuthTokenService>();
builder.Services.AddSingleton<MicrosoftTokenValidator>();
builder.Services.AddSingleton<DocumentRepository>();
builder.Services.AddSingleton<NumberAllocator>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<CodeSeriesRepository>();
builder.Services.AddSingleton<CodeCatalogRepository>();
builder.Services.AddSingleton<CodeImportService>();
builder.Services.AddSingleton<MfaService>();
builder.Services.AddSingleton<AiOrchestratorFactory>();
builder.Services.AddHttpClient();

builder.Build().Run();
