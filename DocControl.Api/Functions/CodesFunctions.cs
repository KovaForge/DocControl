using System.Net;
using System.Text.Json;
using System.Linq;
using System.Web;
using DocControl.Api.Infrastructure;
using DocControl.Core.Models;
using DocControl.Infrastructure.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocControl.Api.Functions;

public sealed class CodesFunctions
{
    private readonly AuthContextFactory authFactory;
    private readonly ProjectMemberRepository memberRepository;
    private readonly DocumentRepository documentRepository;
    private readonly CodeSeriesRepository codeSeriesRepository;
    private readonly CodeCatalogRepository codeCatalogRepository;
    private readonly LevelCodeRepository levelCodeRepository;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly ILogger<CodesFunctions> logger;

    public CodesFunctions(
        AuthContextFactory authFactory,
        ProjectMemberRepository memberRepository,
        DocumentRepository documentRepository,
        CodeSeriesRepository codeSeriesRepository,
        CodeCatalogRepository codeCatalogRepository,
        LevelCodeRepository levelCodeRepository,
        IOptions<JsonSerializerOptions> jsonOptions,
        ILogger<CodesFunctions> logger)
    {
        this.authFactory = authFactory;
        this.memberRepository = memberRepository;
        this.documentRepository = documentRepository;
        this.codeSeriesRepository = codeSeriesRepository;
        this.codeCatalogRepository = codeCatalogRepository;
        this.levelCodeRepository = levelCodeRepository;
        this.jsonOptions = jsonOptions.Value;
        this.logger = logger;
    }

    [Function("LevelCodes_List")]
    public async Task<HttpResponseData> ListLevelCodesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/level-codes")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var query = HttpUtility.ParseQueryString(req.Url.Query);
        int? level = null;
        if (!string.IsNullOrWhiteSpace(query["level"]))
        {
            if (!int.TryParse(query["level"], out var parsed) || parsed < 1 || parsed > 6)
            {
                return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level must be between 1 and 6");
            }
            level = parsed;
        }

        var codes = await levelCodeRepository.ListAsync(projectId, level, req.FunctionContext.CancellationToken);
        return await req.ToJsonAsync(codes, HttpStatusCode.OK, jsonOptions);
    }

    [Function("LevelCodes_Get")]
    public async Task<HttpResponseData> GetLevelCodeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/level-codes/{level:int}/{code}")] HttpRequestData req,
        long projectId,
        int level,
        string code)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");
        if (level < 1 || level > 6) return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level must be between 1 and 6");
        if (string.IsNullOrWhiteSpace(code)) return await req.ErrorAsync(HttpStatusCode.BadRequest, "Code required");

        var record = await levelCodeRepository.GetAsync(projectId, level, code, req.FunctionContext.CancellationToken);
        if (record is null) return await req.ErrorAsync(HttpStatusCode.NotFound, "Level code not found");
        return await req.ToJsonAsync(record, HttpStatusCode.OK, jsonOptions);
    }

    [Function("LevelCodes_Upsert")]
    public async Task<HttpResponseData> UpsertLevelCodeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/{projectId:long}/level-codes")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Contributor, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Contributor role required");

        UpsertLevelCodeRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<UpsertLevelCodeRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid level code payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null) return await req.ErrorAsync(HttpStatusCode.BadRequest, "Payload required");
        if (payload.Level < 1 || payload.Level > 6) return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level must be between 1 and 6");
        if (string.IsNullOrWhiteSpace(payload.Code)) return await req.ErrorAsync(HttpStatusCode.BadRequest, "Code required");

        var record = await levelCodeRepository.UpsertAsync(projectId, payload.Level, payload.Code, payload.Description, req.FunctionContext.CancellationToken);
        return await req.ToJsonAsync(record, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Codes_List")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/codes")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var codes = await codeCatalogRepository.ListAsync(projectId, req.FunctionContext.CancellationToken);
        return await req.ToJsonAsync(codes, HttpStatusCode.OK, jsonOptions);
    }

    [Function("CodeSeries_List")]
    public async Task<HttpResponseData> ListSeriesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/series")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var series = await codeSeriesRepository.ListAsync(projectId, req.FunctionContext.CancellationToken);
        return await req.ToJsonAsync(series, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Codes_Upsert")]
    public async Task<HttpResponseData> UpsertAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/{projectId:long}/codes")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Contributor, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Contributor role required");

        UpsertCodeRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<UpsertCodeRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid code payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Level1))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level1 required");
        }

        var key = new CodeSeriesKey
        {
            ProjectId = projectId,
            Level1 = payload.Level1.Trim(),
            Level2 = payload.Level2.Trim(),
            Level3 = payload.Level3.Trim(),
            Level4 = string.IsNullOrWhiteSpace(payload.Level4) ? null : payload.Level4.Trim(),
            Level5 = string.IsNullOrWhiteSpace(payload.Level5) ? null : payload.Level5.Trim(),
            Level6 = string.IsNullOrWhiteSpace(payload.Level6) ? null : payload.Level6.Trim()
        };
        var id = await codeCatalogRepository.UpsertAsync(key, payload.Description, req.FunctionContext.CancellationToken);
        return await req.ToJsonAsync(new { id }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Codes_Delete")]
    public async Task<HttpResponseData> DeleteAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "projects/{projectId:long}/codes/{codeSeriesId:long}")] HttpRequestData req,
        long projectId,
        long codeSeriesId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Contributor, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Contributor role required");

        await codeCatalogRepository.DeleteAsync(projectId, codeSeriesId, req.FunctionContext.CancellationToken);
        return req.ToJsonAsync(new { deleted = codeSeriesId }, HttpStatusCode.OK, jsonOptions).Result;
    }

    [Function("Codes_Purge")]
    public async Task<HttpResponseData> PurgeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "projects/{projectId:long}/codes/purge")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Owner, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Owner role required");

        try
        {
            var logicalCodes = (await codeCatalogRepository.ListAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false)).Count;
            // Drop documents + audit first to avoid FK violations, then purge code series.
            var deletedDocs = await documentRepository.CountFilteredAsync(projectId, null, null, null, null, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            await documentRepository.ClearAllAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            var deletedCodes = await codeSeriesRepository.PurgeAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            await codeCatalogRepository.PurgeAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            logger.LogInformation("Purged documents {Docs} and codes {Codes} for project {Project}", deletedDocs, deletedCodes, projectId);
            return await req.ToJsonAsync(new { deletedDocuments = deletedDocs, deletedCodes = logicalCodes }, HttpStatusCode.OK, jsonOptions);
        }
        catch (Exception ex)
        {
            return await req.ErrorAsync(HttpStatusCode.InternalServerError, $"Failed to purge codes: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Function("CodeSeries_Purge")]
    public async Task<HttpResponseData> PurgeSeriesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "projects/{projectId:long}/series/purge")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Owner, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Owner role required");

        var docCount = await documentRepository.CountFilteredAsync(projectId, null, null, null, null, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (docCount > 0)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Cannot purge code series while documents exist. Purge documents first.");
        }

        var deletedSeries = await codeSeriesRepository.PurgeAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        return await req.ToJsonAsync(new { deletedSeries }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Codes_ExportJson")]
    public async Task<HttpResponseData> ExportAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/codes/export")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var codes = await codeCatalogRepository.ListAsync(projectId, req.FunctionContext.CancellationToken);
        var payload = codes.Select(c => new
        {
            c.Key.Level1,
            c.Key.Level2,
            c.Key.Level3,
            c.Key.Level4,
            c.Key.Level5,
            c.Key.Level6,
            c.Description
        });
        return await req.ToJsonAsync(payload, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Codes_ImportJson")]
    public async Task<HttpResponseData> ImportJsonAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/{projectId:long}/codes/import/json")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Contributor, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Contributor role required");

        List<UpsertCodeRequest>? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<List<UpsertCodeRequest>>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid code import JSON payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || payload.Count == 0) return await req.ErrorAsync(HttpStatusCode.BadRequest, "No codes provided");

        var imported = 0;
        foreach (var p in payload)
        {
            if (string.IsNullOrWhiteSpace(p.Level1)) continue;
            var key = new CodeSeriesKey
            {
                ProjectId = projectId,
                Level1 = p.Level1.Trim(),
                Level2 = p.Level2.Trim(),
                Level3 = p.Level3.Trim(),
                Level4 = string.IsNullOrWhiteSpace(p.Level4) ? null : p.Level4.Trim(),
                Level5 = string.IsNullOrWhiteSpace(p.Level5) ? null : p.Level5.Trim(),
                Level6 = string.IsNullOrWhiteSpace(p.Level6) ? null : p.Level6.Trim()
            };
            await codeCatalogRepository.UpsertAsync(key, p.Description, req.FunctionContext.CancellationToken);
            imported++;
        }

        return await req.ToJsonAsync(new { imported }, HttpStatusCode.OK, jsonOptions);
    }

    private async Task<bool> IsAtLeast(long projectId, long userId, string requiredRole, CancellationToken cancellationToken)
    {
        var role = await memberRepository.GetRoleAsync(projectId, userId, cancellationToken).ConfigureAwait(false);
        if (role is null) return false;
        return Roles.Compare(role, requiredRole) >= 0;
    }
}

public sealed record UpsertCodeRequest(string Level1, string Level2, string Level3, string? Level4, string? Level5, string? Level6, string? Description, int? NextNumber);

public sealed record UpsertLevelCodeRequest(int Level, string Code, string? Description);
