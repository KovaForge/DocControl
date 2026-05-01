using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocControl.Api.Infrastructure;
using DocControl.Api.Services;
using DocControl.Core.Security;
using DocControl.Infrastructure.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocControl.Api.Functions;

public sealed class AuthFunctions
{
    private readonly UserRepository userRepository;
    private readonly UserAuthRepository userAuthRepository;
    private readonly AuthContextFactory authFactory;
    private readonly AuthTokenService authTokenService;
    private readonly MicrosoftTokenValidator microsoftTokenValidator;
    private readonly MfaService mfaService;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly ILogger<AuthFunctions> logger;
    private const int BackupCodeCount = 10;
    private const int BackupCodeLength = 10;
    private const int BackupCodeGroupSize = 5;

    public AuthFunctions(
        UserRepository userRepository,
        UserAuthRepository userAuthRepository,
        AuthContextFactory authFactory,
        AuthTokenService authTokenService,
        MicrosoftTokenValidator microsoftTokenValidator,
        MfaService mfaService,
        IOptions<JsonSerializerOptions> jsonOptions,
        ILogger<AuthFunctions> logger)
    {
        this.userRepository = userRepository;
        this.userAuthRepository = userAuthRepository;
        this.authFactory = authFactory;
        this.authTokenService = authTokenService;
        this.microsoftTokenValidator = microsoftTokenValidator;
        this.mfaService = mfaService;
        this.jsonOptions = jsonOptions.Value;
        this.logger = logger;
    }

    [Function("Auth_Microsoft_DeviceCode_Config")]
    public async Task<HttpResponseData> GetMicrosoftDeviceCodeConfigAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/microsoft/device-code/config")] HttpRequestData req)
    {
        var options = microsoftTokenValidator.GetDeviceCodeOptions();
        if (options is null)
        {
            return await req.ErrorAsync(HttpStatusCode.ServiceUnavailable, "Microsoft CLI login is not configured");
        }

        return await req.ToJsonAsync(new
        {
            options.ClientId,
            options.TenantId,
            options.Scopes
        }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Microsoft_Cli_Token")]
    public async Task<HttpResponseData> ExchangeMicrosoftCliTokenAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/microsoft/cli-token")] HttpRequestData req)
    {
        MicrosoftCliTokenRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<MicrosoftCliTokenRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid Microsoft CLI token payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.IdToken))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Microsoft id token is required");
        }

        var microsoftUser = await microsoftTokenValidator
            .ValidateIdTokenAsync(payload.IdToken, req.FunctionContext.CancellationToken)
            .ConfigureAwait(false);
        if (microsoftUser is null)
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid Microsoft token");
        }

        var user = await userRepository.GetByEmailAsync(microsoftUser.Email, req.FunctionContext.CancellationToken).ConfigureAwait(false)
                   ?? await userRepository.RegisterAsync(microsoftUser.Email, microsoftUser.DisplayName, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        await userAuthRepository.EnsureExistsAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var auth = await userAuthRepository.GetAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var authToken = authTokenService.IssueToken(user.Id, user.Email);
        if (string.IsNullOrWhiteSpace(authToken))
        {
            return await req.ErrorAsync(HttpStatusCode.InternalServerError, "DocControl token issuing is not configured");
        }

        return await req.ToJsonAsync(new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            MfaEnabled = auth?.MfaEnabled ?? false,
            AuthToken = authToken,
            RequiresPasswordReset = false,
            Provider = "microsoft"
        }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Register")]
    public async Task<HttpResponseData> RegisterAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData req)
    {
        RegisterRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<RegisterRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid register payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Email))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Email is required");
        }
        if (string.IsNullOrWhiteSpace(payload.Password))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Password is required");
        }

        var displayName = string.IsNullOrWhiteSpace(payload.DisplayName) ? payload.Email.Trim() : payload.DisplayName.Trim();
        var user = await userRepository.RegisterAsync(payload.Email.Trim(), displayName, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var existingAuth = await userRepository.GetPasswordAuthByIdAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (existingAuth?.PasswordHash is not null)
        {
            return await req.ErrorAsync(HttpStatusCode.Conflict, "Registration not available.");
        }

        var (hash, salt, keySalt) = PasswordHasher.HashPassword(payload.Password);
        await userRepository.SetPasswordAsync(user.Id, hash, salt, keySalt, req.FunctionContext.CancellationToken).ConfigureAwait(false);

        await userAuthRepository.EnsureExistsAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var auth = await userAuthRepository.GetAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var mfaEnabled = auth?.MfaEnabled ?? false;
        var authToken = authTokenService.IssueToken(user.Id, user.Email);
        return await req.ToJsonAsync(new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            user.CreatedAtUtc,
            MfaEnabled = mfaEnabled,
            AuthToken = authToken,
            RequiresPasswordReset = false
        }, HttpStatusCode.Created, jsonOptions);
    }

    [Function("Auth_Login")]
    public async Task<HttpResponseData> LoginAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        LoginRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<LoginRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid login payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Email) || string.IsNullOrWhiteSpace(payload.Password))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Email and password are required");
        }

        var user = await userRepository.GetPasswordAuthByEmailAsync(payload.Email.Trim(), req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash) || string.IsNullOrWhiteSpace(user.PasswordSalt))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid email or password");
        }

        if (!PasswordHasher.Verify(payload.Password, user.PasswordHash, user.PasswordSalt))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid email or password");
        }

        await userAuthRepository.EnsureExistsAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var auth = await userAuthRepository.GetAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var mfaEnabled = auth?.MfaEnabled ?? false;
        var authToken = authTokenService.IssueToken(user.Id, user.Email);

        return await req.ToJsonAsync(new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            MfaEnabled = mfaEnabled,
            AuthToken = authToken,
            RequiresPasswordReset = false
        }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Password_Initial")]
    public async Task<HttpResponseData> SetInitialPasswordAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/password/initial")] HttpRequestData req)
    {
        InitialPasswordRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<InitialPasswordRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid initial password payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Email) || string.IsNullOrWhiteSpace(payload.Password))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Email and password are required");
        }

        var user = await userRepository.GetPasswordAuthByEmailAsync(payload.Email.Trim(), req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (user is null || !string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid credentials");
        }

        var (hash, salt, keySalt) = PasswordHasher.HashPassword(payload.Password);
        await userRepository.SetPasswordAsync(user.Id, hash, salt, keySalt, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        await userAuthRepository.EnsureExistsAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var auth = await userAuthRepository.GetAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var mfaEnabled = auth?.MfaEnabled ?? false;
        var authToken = authTokenService.IssueToken(user.Id, user.Email);

        return await req.ToJsonAsync(new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            MfaEnabled = mfaEnabled,
            AuthToken = authToken,
            RequiresPasswordReset = false
        }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Password_Change")]
    public async Task<HttpResponseData> ChangePasswordAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/password/change")] HttpRequestData req)
    {
        var (ok, authContext, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || authContext is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");

        ChangePasswordRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<ChangePasswordRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid change password payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.CurrentPassword) || string.IsNullOrWhiteSpace(payload.NewPassword))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Current and new passwords are required");
        }

        var user = await userRepository.GetPasswordAuthByIdAsync(authContext.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (user?.PasswordHash is null || user.PasswordSalt is null || user.KeySalt is null)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Password not set");
        }

        if (!PasswordHasher.Verify(payload.CurrentPassword, user.PasswordHash, user.PasswordSalt))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid current password");
        }

        var (newHash, newSalt, newKeySalt) = PasswordHasher.HashPassword(payload.NewPassword);

        var (openAiEncrypted, geminiEncrypted) = await userRepository.GetAiKeysEncryptedAsync(user.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        string? openAiPlain = null;
        string? geminiPlain = null;

        var oldProtector = new AesGcmSecretProtector(PasswordHasher.DeriveEncryptionKey(user.PasswordHash, user.KeySalt));
        if (!string.IsNullOrWhiteSpace(openAiEncrypted))
        {
            openAiPlain = oldProtector.Decrypt(openAiEncrypted);
        }
        if (!string.IsNullOrWhiteSpace(geminiEncrypted))
        {
            geminiPlain = oldProtector.Decrypt(geminiEncrypted);
        }

        var newProtector = new AesGcmSecretProtector(PasswordHasher.DeriveEncryptionKey(newHash, newKeySalt));
        var newOpenAiEncrypted = string.IsNullOrWhiteSpace(openAiPlain) ? null : newProtector.Encrypt(openAiPlain);
        var newGeminiEncrypted = string.IsNullOrWhiteSpace(geminiPlain) ? null : newProtector.Encrypt(geminiPlain);

        await userRepository.SetPasswordAsync(user.Id, newHash, newSalt, newKeySalt, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        await userRepository.SaveAiKeysEncryptedAsync(user.Id, newOpenAiEncrypted, newGeminiEncrypted, false, false, req.FunctionContext.CancellationToken).ConfigureAwait(false);

        return await req.ToJsonAsync(new { status = "ok" }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Profile_Update")]
    public async Task<HttpResponseData> UpdateProfileAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/profile")] HttpRequestData req)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");

        UpdateProfileRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<UpdateProfileRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid update profile payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.DisplayName))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Display name is required");
        }

        var displayName = payload.DisplayName.Trim();
        await userRepository.UpdateDisplayNameAsync(auth.UserId, displayName, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        return await req.ToJsonAsync(new { DisplayName = displayName }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Me")]
    public async Task<HttpResponseData> MeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")] HttpRequestData req)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");

        var authRecord = await userAuthRepository.GetAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var passwordAuth = await userRepository.GetPasswordAuthByIdAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var hasPassword = !string.IsNullOrWhiteSpace(passwordAuth?.PasswordHash);
        return await req.ToJsonAsync(new
        {
            auth.UserId,
            Email = auth.Email,
            DisplayName = auth.DisplayName,
            MfaEnabled = authRecord?.MfaEnabled ?? false,
            HasPassword = hasPassword
        }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Mfa_Start")]
    public async Task<HttpResponseData> StartMfaAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/mfa/start")] HttpRequestData req)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");

        var secret = mfaService.GenerateSecret();
        var existing = await userAuthRepository.GetAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (existing?.MfaEnabled == true && !string.IsNullOrWhiteSpace(existing.MfaMethodsJson))
        {
            TotpState? state;
            try
            {
                state = JsonSerializer.Deserialize<TotpState>(existing.MfaMethodsJson);
            }
            catch (JsonException)
            {
                state = null;
            }

            if (state is not null && !string.IsNullOrWhiteSpace(state.Secret))
            {
                var pending = new TotpState(
                    state.Secret,
                    state.CreatedAtUtc,
                    state.VerifiedAtUtc,
                    secret,
                    DateTime.UtcNow,
                    state.BackupCodes);
                await userAuthRepository.SaveTotpStateAsync(auth.UserId, pending, true, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                await userAuthRepository.SaveTotpAsync(auth.UserId, secret, verified: false, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await userAuthRepository.SaveTotpAsync(auth.UserId, secret, verified: false, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        }

        var uri = mfaService.BuildOtpAuthUri(secret, auth.Email);
        return await req.ToJsonAsync(new { secret, otpauthUrl = uri }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Mfa_Verify")]
    public async Task<HttpResponseData> VerifyMfaAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/mfa/verify")] HttpRequestData req)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");

        VerifyMfaRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<VerifyMfaRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid verify payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Code))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Code required");
        }

        var authRecord = await userAuthRepository.GetAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (authRecord?.MfaMethodsJson is null)
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Start MFA setup first");
        }

        TotpState? state;
        try
        {
            state = JsonSerializer.Deserialize<TotpState>(authRecord.MfaMethodsJson);
        }
        catch (JsonException)
        {
            state = null;
        }

        var hasBackupCodes = state?.BackupCodes is { Count: > 0 };
        if (state is null || (string.IsNullOrWhiteSpace(state.Secret) && !hasBackupCodes))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "MFA secret missing");
        }

        if (!string.IsNullOrWhiteSpace(state.PendingSecret))
        {
            if (!mfaService.VerifyCode(state.PendingSecret, payload.Code))
            {
                if (TryConsumeBackupCode(state, payload.Code, out var updated))
                {
                    await userAuthRepository.SaveTotpStateAsync(auth.UserId, updated, true, req.FunctionContext.CancellationToken).ConfigureAwait(false);
                    return await req.ToJsonAsync(new { mfaEnabled = true }, HttpStatusCode.OK, jsonOptions);
                }

                return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid code");
            }

            var promoted = new TotpState(
                state.PendingSecret,
                state.PendingCreatedAtUtc ?? DateTime.UtcNow,
                DateTime.UtcNow,
                null,
                null,
                state.BackupCodes);
            await userAuthRepository.SaveTotpStateAsync(auth.UserId, promoted, true, req.FunctionContext.CancellationToken).ConfigureAwait(false);
            return await req.ToJsonAsync(new { mfaEnabled = true }, HttpStatusCode.OK, jsonOptions);
        }

        if (string.IsNullOrWhiteSpace(state.Secret) || !mfaService.VerifyCode(state.Secret, payload.Code))
        {
            if (TryConsumeBackupCode(state, payload.Code, out var updated))
            {
                await userAuthRepository.SaveTotpStateAsync(auth.UserId, updated, true, req.FunctionContext.CancellationToken).ConfigureAwait(false);
                return await req.ToJsonAsync(new { mfaEnabled = true }, HttpStatusCode.OK, jsonOptions);
            }

            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid code");
        }

        var verified = new TotpState(state.Secret, state.CreatedAtUtc, DateTime.UtcNow, null, null, state.BackupCodes);
        await userAuthRepository.SaveTotpStateAsync(auth.UserId, verified, true, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        return await req.ToJsonAsync(new { mfaEnabled = true }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Mfa_Backup")]
    public async Task<HttpResponseData> BackupCodesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/mfa/backup")] HttpRequestData req)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");
        if (!auth.MfaEnabled) return await req.ErrorAsync(HttpStatusCode.Forbidden, "MFA required");

        var authRecord = await userAuthRepository.GetAsync(auth.UserId, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        var state = TryReadTotpState(authRecord?.MfaMethodsJson);
        if (state is null || string.IsNullOrWhiteSpace(state.Secret))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "MFA not configured");
        }

        var codes = GenerateBackupCodes();
        var hashedCodes = HashBackupCodes(codes);
        var updated = new TotpState(
            state.Secret,
            state.CreatedAtUtc,
            state.VerifiedAtUtc,
            state.PendingSecret,
            state.PendingCreatedAtUtc,
            hashedCodes);
        await userAuthRepository.SaveTotpStateAsync(auth.UserId, updated, true, req.FunctionContext.CancellationToken).ConfigureAwait(false);

        return await req.ToJsonAsync(new { codes }, HttpStatusCode.OK, jsonOptions);
    }

    [Function("Auth_Link")]
    public async Task<HttpResponseData> LinkAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/link")] HttpRequestData req)
    {
        var (ok, auth, _) = await authFactory.BindAsync(req, req.FunctionContext.CancellationToken);
        if (!ok || auth is null) return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Auth required");

        LinkAccountRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<LinkAccountRequest>(req.Body, jsonOptions, req.FunctionContext.CancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid link payload");
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Invalid JSON payload");
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.LegacyEmail) ||
            string.IsNullOrWhiteSpace(payload.Password) ||
            string.IsNullOrWhiteSpace(payload.MfaCode))
        {
            return await req.ErrorAsync(HttpStatusCode.BadRequest, "Legacy email, password, and MFA code are required");
        }

        var legacy = await userRepository.GetPasswordAuthByEmailAsync(payload.LegacyEmail.Trim(), req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (legacy is null || string.IsNullOrWhiteSpace(legacy.PasswordHash) || string.IsNullOrWhiteSpace(legacy.PasswordSalt))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid credentials");
        }

        if (!PasswordHasher.Verify(payload.Password, legacy.PasswordHash, legacy.PasswordSalt))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid credentials");
        }

        var legacyAuth = await userAuthRepository.GetAsync(legacy.Id, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (legacyAuth?.MfaMethodsJson is null || !legacyAuth.MfaEnabled)
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid credentials");
        }

        TotpState? state;
        try
        {
            state = JsonSerializer.Deserialize<TotpState>(legacyAuth.MfaMethodsJson);
        }
        catch (JsonException)
        {
            state = null;
        }

        if (state is null || string.IsNullOrWhiteSpace(state.Secret))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid credentials");
        }

        if (!mfaService.VerifyCode(state.Secret, payload.MfaCode))
        {
            return await req.ErrorAsync(HttpStatusCode.Unauthorized, "Invalid credentials");
        }

        var linked = await userRepository.LinkAccountAsync(auth.UserId, legacy.Id, auth.Email, auth.DisplayName, req.FunctionContext.CancellationToken).ConfigureAwait(false);
        if (!linked)
        {
            return await req.ErrorAsync(HttpStatusCode.Conflict, "Current account already has data; cannot link.");
        }

        return await req.ToJsonAsync(new { status = "ok", userId = legacy.Id }, HttpStatusCode.OK, jsonOptions);
    }

    private static TotpState? TryReadTotpState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TotpState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GenerateBackupCodes()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var codes = new List<string>(BackupCodeCount);
        for (var i = 0; i < BackupCodeCount; i++)
        {
            var chars = new char[BackupCodeLength];
            for (var j = 0; j < BackupCodeLength; j++)
            {
                var index = RandomNumberGenerator.GetInt32(alphabet.Length);
                chars[j] = alphabet[index];
            }
            var raw = new string(chars);
            codes.Add(raw.Insert(BackupCodeGroupSize, "-"));
        }
        return codes;
    }

    private static List<DocControl.Infrastructure.Data.BackupCode> HashBackupCodes(IEnumerable<string> codes)
    {
        var hashed = new List<DocControl.Infrastructure.Data.BackupCode>();
        foreach (var code in codes)
        {
            var normalized = NormalizeBackupCode(code);
            if (normalized.Length == 0) continue;
            hashed.Add(HashBackupCode(normalized));
        }
        return hashed;
    }

    private static DocControl.Infrastructure.Data.BackupCode HashBackupCode(string normalized)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var codeBytes = Encoding.UTF8.GetBytes(normalized);
        var data = new byte[salt.Length + codeBytes.Length];
        Buffer.BlockCopy(salt, 0, data, 0, salt.Length);
        Buffer.BlockCopy(codeBytes, 0, data, salt.Length, codeBytes.Length);
        var hash = SHA256.HashData(data);
        return new DocControl.Infrastructure.Data.BackupCode(Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private static bool TryConsumeBackupCode(TotpState state, string input, out TotpState updated)
    {
        updated = state;
        var normalized = NormalizeBackupCode(input);
        if (normalized.Length == 0 || state.BackupCodes is null || state.BackupCodes.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < state.BackupCodes.Count; i++)
        {
            var entry = state.BackupCodes[i];
            if (TryMatchBackupCode(entry, normalized))
            {
                var remaining = state.BackupCodes.Where((_, idx) => idx != i).ToList();
                updated = new TotpState(
                    state.Secret,
                    state.CreatedAtUtc,
                    DateTime.UtcNow,
                    state.PendingSecret,
                    state.PendingCreatedAtUtc,
                    remaining.Count == 0 ? null : remaining);
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchBackupCode(DocControl.Infrastructure.Data.BackupCode entry, string normalized)
    {
        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(entry.Salt);
            expectedHash = Convert.FromBase64String(entry.Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        var codeBytes = Encoding.UTF8.GetBytes(normalized);
        var data = new byte[salt.Length + codeBytes.Length];
        Buffer.BlockCopy(salt, 0, data, 0, salt.Length);
        Buffer.BlockCopy(codeBytes, 0, data, salt.Length, codeBytes.Length);
        var hash = SHA256.HashData(data);
        return CryptographicOperations.FixedTimeEquals(hash, expectedHash);
    }

    private static string NormalizeBackupCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var builder = new StringBuilder(code.Length);
        foreach (var ch in code)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }
        return builder.ToString();
    }
}

internal sealed record RegisterRequest(string Email, string? DisplayName, string Password);
internal sealed record LoginRequest(string Email, string Password);
internal sealed record InitialPasswordRequest(string Email, string Password);
internal sealed record MicrosoftCliTokenRequest(string IdToken);
internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
internal sealed record UpdateProfileRequest(string DisplayName);
internal sealed record VerifyMfaRequest(string Code);
internal sealed record LinkAccountRequest(string LegacyEmail, string Password, string MfaCode);
