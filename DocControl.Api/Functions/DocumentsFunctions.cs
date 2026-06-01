using System.Net;
using System.Text.Json;
using System.Web;
using System.Linq;
using DocControl.Api.Infrastructure;
using DocControl.Core.Configuration;
using DocControl.Core.Models;
using DocControl.Infrastructure.Data;
using DocControl.Infrastructure.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocControl.Api.Functions;

public sealed class DocumentsFunctions
{
    private readonly AuthContextFactory authFactory;
    private readonly ProjectMemberRepository memberRepository;
    private readonly DocumentRepository documentRepository;
    private readonly NumberAllocator allocator;
    private readonly AuditRepository auditRepository;
    private readonly ConfigService configService;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly ILogger<DocumentsFunctions> logger;

    public DocumentsFunctions(
        AuthContextFactory authFactory,
        ProjectMemberRepository memberRepository,
        DocumentRepository documentRepository,
        NumberAllocator allocator,
        AuditRepository auditRepository,
        ConfigService configService,
        IOptions<JsonSerializerOptions> jsonOptions,
        ILogger<DocumentsFunctions> logger)
    {
        this.authFactory = authFactory;
        this.memberRepository = memberRepository;
        this.documentRepository = documentRepository;
        this.allocator = allocator;
        this.auditRepository = auditRepository;
        this.configService = configService;
        this.jsonOptions = jsonOptions.Value;
        this.logger = logger;
    }

    [Function("Documents_List")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/documents")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var l1 = query["level1"];
        var l2 = query["level2"];
        var l3 = query["level3"];
        var filter = query["q"];
        var take = int.TryParse(query["take"], out var t) ? Math.Clamp(t, 1, 200) : 50;
        var skip = int.TryParse(query["skip"], out var s) ? Math.Max(s, 0) : 0;

        var total = await documentRepository.CountFilteredAsync(projectId, l1, l2, l3, filter, req.FunctionContext.CancellationToken);
        var docs = await documentRepository.GetFilteredAsync(projectId, l1, l2, l3, filter, take, skip, req.FunctionContext.CancellationToken);
        return await req.ToJsonAsync(new { items = docs, total, skip, take }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Documents_ExportJson")]
    public async Task<HttpResponseData> ExportAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/documents/export")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var config = await configService.LoadDocumentConfigAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var docs = await documentRepository.GetAllAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var payload = docs.Select(d => new
        {
            Code = FormatCode(d, config),
            d.FreeText,
            d.FileName,
            d.CreatedAtUtc
        });
        return await req.ToJsonAsync(payload, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Documents_Get")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/documents/{documentId:long}")] HttpRequestData req,
        long projectId,
        long documentId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var doc = await documentRepository.GetByIdAsync(projectId, documentId, req.FunctionContext.CancellationToken);
        if (doc is null) return await req.ErrorAsync(HttpStatusCode.NotFound, "Not found");
        return await req.ToJsonAsync(doc, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Documents_Create")]
    public async Task<HttpResponseData> CreateAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/{projectId:long}/documents")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Contributor, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Contributor role required");

        CreateDocumentRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<CreateDocumentRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid document payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Payload required");
        }

        var config = await configService.LoadDocumentConfigAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var levelCount = Math.Clamp(config.LevelCount, 1, 6);
        if (levelCount >= 1 && string.IsNullOrWhiteSpace(payload.Level1))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level1 required");
        }
        if (levelCount >= 2 && string.IsNullOrWhiteSpace(payload.Level2))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level2 required");
        }
        if (levelCount >= 3 && string.IsNullOrWhiteSpace(payload.Level3))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level3 required");
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

        var allocated = await allocator.AllocateAsync(key, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var generator = new CodeGenerator(config);
        var fileName = generator.BuildFileName(key, allocated.Number, payload.FreeText ?? string.Empty, payload.Extension);
        var now = DateTime.UtcNow;

        var docId = await documentRepository.InsertAsync(allocated, key, payload.FreeText ?? string.Empty, fileName, auth.UserId, now, payload.OriginalQuery, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        await auditRepository.InsertAsync(projectId, "DocumentCreated", fileName, auth.UserId, now, docId, req.FunctionContext.CancellationToken).ConfigureAwait(false);

        return await req.ToJsonAsync(new { id = docId, number = allocated.Number, fileName }, HttpStatusCode.Created, jsonOptions);
    }

    [Function("Documents_Preview")]
    public async Task<HttpResponseData> PreviewAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/{projectId:long}/documents/preview")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        CreateDocumentRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<CreateDocumentRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid document preview payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Payload required");
        }

        var config = await configService.LoadDocumentConfigAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var levelCount = Math.Clamp(config.LevelCount, 1, 6);
        if (levelCount >= 1 && string.IsNullOrWhiteSpace(payload.Level1))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level1 required");
        }
        if (levelCount >= 2 && string.IsNullOrWhiteSpace(payload.Level2))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level2 required");
        }
        if (levelCount >= 3 && string.IsNullOrWhiteSpace(payload.Level3))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Level3 required");
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

        var nextNumber = await allocator.PeekNextAsync(key, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var generator = new CodeGenerator(config);
        var fileName = generator.BuildFileName(key, nextNumber, payload.FreeText ?? string.Empty, payload.Extension);

        return await req.ToJsonAsync(new { number = nextNumber, fileName }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Documents_Delete")]
    public async Task<HttpResponseData> DeleteAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "projects/{projectId:long}/documents/{documentId:long}")] HttpRequestData req,
        long projectId,
        long documentId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Editor, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Editor role required");

        var doc = await documentRepository.GetByIdAsync(projectId, documentId, req.FunctionContext.CancellationToken);
        if (doc is null) return await req.ErrorAsync(HttpStatusCode.NotFound, "Not found");

        var affected = await documentRepository.DeleteAsync(projectId, documentId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        await auditRepository.InsertAsync(projectId, "DocumentDeleted", doc.FileName, auth.UserId, DateTime.UtcNow, documentId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        return await req.ToJsonAsync(new { deleted = affected > 0, documentId }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Documents_Purge")]
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "projects/{projectId:long}/documents")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Owner, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Owner role required");

        try
        {
            var deleted = await documentRepository.CountFilteredAsync(projectId, null, null, null, null, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            await documentRepository.ClearAllAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            await auditRepository.InsertAsync(projectId, "DocumentsPurged", $"deleted:{deleted}", auth.UserId, DateTime.UtcNow, null, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            return await req.ToJsonAsync(new { deleted }, HttpStatusCode.OK, jsonOptions);
        }
        catch (Exception ex)
        {
            return await req.ErrorAsync(HttpStatusCode.InternalServerError, $"Failed to purge documents: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Function("Audit_List")]
    public async Task<HttpResponseData> AuditListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/audit")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (!await IsAtLeast(projectId, auth.UserId, Roles.Viewer, req.FunctionContext.CancellationToken)) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Access denied");

        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var action = query["action"];
        var user = query["user"];
        var take = int.TryParse(query["take"], out var t) ? t : 50;
        var skip = int.TryParse(query["skip"], out var s) ? s : 0;

        var entries = await auditRepository.GetPagedAsync(projectId, take, skip, action, user, req.FunctionContext.CancellationToken);
        return await req.ToJsonAsync(entries, HttpStatusCode.OK, jsonOptions);
    }

    private async Task<bool> IsAtLeast(long projectId, long userId, string requiredRole, CancellationToken cancellationToken)
    {
        var role = await memberRepository.GetRoleAsync(projectId, userId, cancellationToken).ConfigureAwait(false);
        if (role is null) return false;
        return Roles.Compare(role, requiredRole) >= 0;
    }

    private static string FormatCode(DocumentRecord d, DocumentConfig config)
    {
        var sep = string.IsNullOrEmpty(config.Separator) ? "-" : config.Separator;
        var padding = config.PaddingLength <= 0 ? 3 : config.PaddingLength;
        var padded = d.Number.ToString().PadLeft(padding, '0');
        var levelCount = Math.Clamp(config.LevelCount, 1, 6);
        var parts = new List<string>();
        if (levelCount >= 1) parts.Add(d.Level1);
        if (levelCount >= 2) parts.Add(d.Level2);
        if (levelCount >= 3) parts.Add(d.Level3);
        if (levelCount >= 4 && !string.IsNullOrWhiteSpace(d.Level4)) parts.Add(d.Level4);
        if (levelCount >= 5 && !string.IsNullOrWhiteSpace(d.Level5)) parts.Add(d.Level5);
        if (levelCount >= 6 && !string.IsNullOrWhiteSpace(d.Level6)) parts.Add(d.Level6);
        parts.Add(padded);
        return string.Join(sep, parts);
    }
}

public sealed record CreateDocumentRequest(string Level1, string Level2, string Level3, string? Level4, string? Level5, string? Level6, string? FreeText, string? Extension, string? OriginalQuery);
