using System.Net;
using System.Text.Json;
using DocControl.Api.Infrastructure;
using DocControl.Core.Configuration;
using DocControl.Infrastructure.Data;
using DocControl.Infrastructure.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocControl.Api.Functions;

public sealed class SettingsFunctions
{
    private readonly AuthContextFactory authFactory;
    private readonly ConfigService configService;
    private readonly ProjectRepository projectRepository;
    private readonly AgentTokenRepository agentTokenRepository;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly ILogger<SettingsFunctions> logger;

    public SettingsFunctions(
        AuthContextFactory authFactory,
        ConfigService configService,
        ProjectRepository projectRepository,
        AgentTokenRepository agentTokenRepository,
        IOptions<JsonSerializerOptions> jsonOptions,
        ILogger<SettingsFunctions> logger)
    {
        this.authFactory = authFactory;
        this.configService = configService;
        this.projectRepository = projectRepository;
        this.agentTokenRepository = agentTokenRepository;
        this.jsonOptions = jsonOptions.Value;
        this.logger = logger;
    }

    [Function("Settings_Get")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/settings")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (auth.IsAgentToken) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Interactive session required");

        if (!await projectRepository.IsMemberAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false))
        {
            return await req.ErrorAsync(HttpStatusCode.Forbidden, "Not a project member.");
        }

        var aiSettings = await configService.LoadAiSettingsAsync(projectId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var (hasOpenAi, hasGemini) = await configService.GetAiKeyStatusAsync(aiSettings, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var (openAiSuffix, geminiSuffix) = await configService.GetAiKeySuffixesAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);

        return await req.ToJsonAsync(new ProjectSettingsResponse(aiSettings, hasOpenAi, hasGemini, openAiSuffix, geminiSuffix), HttpStatusCode.OK, jsonOptions);
    }

    [Function("Settings_Save")]
    public async Task<HttpResponseData> SaveAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/{projectId:long}/settings")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (auth.IsAgentToken) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Interactive session required");

        if (!await projectRepository.IsMemberAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false))
        {
            return await req.ErrorAsync(HttpStatusCode.Forbidden, "Not a project member.");
        }

        SaveProjectSettingsRequest? payload;
        try
        {
            var raw = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return await req.ErrorAsync(HttpStatusCode.BadRequest, "Empty payload.");
            }
            payload = JsonSerializer.Deserialize<SaveProjectSettingsRequest>(raw, jsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid settings payload.");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, $"Invalid JSON payload: {ex.Message}");
        }

        if (payload?.AiSettings is null)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "AiSettings are required.");
        }

        try
        {
            await configService.SaveAiSettingsAsync(
                projectId,
                auth.UserId,
                payload.AiSettings,
                payload.OpenAiKey ?? string.Empty,
                payload.GeminiKey ?? string.Empty,
                payload.ClearOpenAiKey,
                payload.ClearGeminiKey,
                req.FunctionContext.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, ex.Message);
        }

        var (hasOpenAi, hasGemini) = await configService.GetAiKeyStatusAsync(payload.AiSettings, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var (openAiSuffix, geminiSuffix) = await configService.GetAiKeySuffixesAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        return await req.ToJsonAsync(new { status = "ok", hasOpenAi, hasGemini, openAiKeySuffix = openAiSuffix, geminiKeySuffix = geminiSuffix }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("AgentTokens_List")]
    public async Task<HttpResponseData> ListAgentTokensAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId:long}/agent-tokens")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (auth.IsAgentToken) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Interactive session required");

        if (!await projectRepository.IsMemberAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false))
        {
            return await req.ErrorAsync(HttpStatusCode.Forbidden, "Not a project member.");
        }

        var tokens = await agentTokenRepository.ListActiveAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        return await req.ToJsonAsync(new { items = tokens.Select(ToAgentTokenResponse) }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("AgentTokens_Create")]
    public async Task<HttpResponseData> CreateAgentTokenAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/{projectId:long}/agent-tokens")] HttpRequestData req,
        long projectId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (auth.IsAgentToken) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Interactive session required");

        if (!await projectRepository.IsMemberAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false))
        {
            return await req.ErrorAsync(HttpStatusCode.Forbidden, "Not a project member.");
        }

        CreateAgentTokenRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<CreateAgentTokenRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid agent token payload.");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload.");
        }

        var expiresAtUtc = payload?.ExpiresAtUtc;
        if (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Expiry must be in the future.");
        }

        var created = await agentTokenRepository.CreateAsync(auth.UserId, payload?.Name, expiresAtUtc, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        return await req.ToJsonAsync(new
        {
            token = created.Token,
            item = ToAgentTokenResponse(created.Record)
        }, HttpStatusCode.Created, jsonOptions);
    }

    [Function("AgentTokens_Revoke")]
    public async Task<HttpResponseData> RevokeAgentTokenAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "projects/{projectId:long}/agent-tokens/{tokenId:long}")] HttpRequestData req,
        long projectId,
        long tokenId)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");
        if (auth.IsAgentToken) return await req.ErrorAsync(HttpStatusCode.Forbidden, "Interactive session required");

        if (!await projectRepository.IsMemberAsync(projectId, auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false))
        {
            return await req.ErrorAsync(HttpStatusCode.Forbidden, "Not a project member.");
        }

        var revoked = await agentTokenRepository.RevokeAsync(auth.UserId, tokenId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (!revoked) return await req.ErrorAsync(HttpStatusCode.NotFound, "Token not found.");
        return await req.ToJsonAsync(new { revoked = true, tokenId }, HttpStatusCode.OK, jsonOptions);
    }

    private static AgentTokenResponse ToAgentTokenResponse(DocControl.Core.Models.AgentTokenRecord token) =>
        new(
            token.Id,
            token.Name,
            token.Prefix,
            token.CreatedAtUtc,
            token.LastUsedAtUtc,
            token.ExpiresAtUtc);
}

public sealed record ProjectSettingsResponse(AiSettings AiSettings, bool HasOpenAiKey, bool HasGeminiKey, string? OpenAiKeySuffix, string? GeminiKeySuffix);

public sealed record SaveProjectSettingsRequest(
    AiSettings AiSettings,
    string? OpenAiKey,
    string? GeminiKey,
    bool ClearOpenAiKey,
    bool ClearGeminiKey);

public sealed record CreateAgentTokenRequest(string? Name, DateTime? ExpiresAtUtc);

public sealed record AgentTokenResponse(
    long Id,
    string Name,
    string Prefix,
    DateTime CreatedAtUtc,
    DateTime? LastUsedAtUtc,
    DateTime? ExpiresAtUtc);
