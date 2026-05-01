using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocControl.Core.Models;
using DocControl.Infrastructure.Data;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DocControl.Api.Infrastructure;

public sealed record AuthContext(long UserId, string Email, string DisplayName, bool MfaEnabled);

public sealed class AuthContextFactory
{
    private readonly UserRepository userRepository;
    private readonly UserAuthRepository userAuthRepository;
    private readonly AuthTokenService authTokenService;
    private readonly bool allowLegacyHeader;

    public AuthContextFactory(
        UserRepository userRepository,
        UserAuthRepository userAuthRepository,
        AuthTokenService authTokenService,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        this.userRepository = userRepository;
        this.userAuthRepository = userAuthRepository;
        this.authTokenService = authTokenService;
        allowLegacyHeader = environment.IsDevelopment()
                            || configuration.GetValue<bool>("ALLOW_LEGACY_AUTH");
    }

    public async Task<(bool ok, AuthContext? context, HttpResponseData? error)> BindAsync(HttpRequestData req, CancellationToken cancellationToken = default)
    {
        if (TryGetSwaPrincipal(req, out var principal))
        {
            var email = GetClaimValue(principal, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")
                        ?? principal.UserDetails
                        ?? GetClaimValue(principal, "preferred_username")
                        ?? GetClaimValue(principal, "name");
            if (string.IsNullOrWhiteSpace(email))
            {
                return (false, null, await req.ErrorAsync(System.Net.HttpStatusCode.Unauthorized, "Missing authenticated user email"));
            }

            var displayName = GetClaimValue(principal, "name") ?? email;
            var user = await userRepository.GetByEmailAsync(email, cancellationToken).ConfigureAwait(false)
                       ?? await userRepository.RegisterAsync(email, displayName, cancellationToken).ConfigureAwait(false);

            await userAuthRepository.EnsureExistsAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return (true, new AuthContext(user.Id, user.Email, user.DisplayName, true), null);
        }

        if (TryGetBearer(req, out var token) && authTokenService.TryValidate(token, out var tokenUserId, out var tokenEmail))
        {
            var user = await userRepository.GetByIdAsync(tokenUserId, cancellationToken).ConfigureAwait(false);
            if (user is not null && string.Equals(user.Email, tokenEmail, StringComparison.OrdinalIgnoreCase))
            {
                await userAuthRepository.EnsureExistsAsync(user.Id, cancellationToken).ConfigureAwait(false);
                var auth = await userAuthRepository.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
                var mfaEnabled = auth?.MfaEnabled ?? false;
                return (true, new AuthContext(user.Id, user.Email, user.DisplayName, mfaEnabled), null);
            }

            return (false, null, await req.ErrorAsync(System.Net.HttpStatusCode.Unauthorized, "Invalid token"));
        }

        if (!allowLegacyHeader)
        {
            return (false, null, await req.ErrorAsync(System.Net.HttpStatusCode.Unauthorized, "Missing authenticated user"));
        }

        if (!req.Headers.TryGetValues("x-user-id", out var userIds))
        {
            return (false, null, await req.ErrorAsync(System.Net.HttpStatusCode.Unauthorized, "Missing user id header"));
        }

        var rawId = userIds.FirstOrDefault();
        if (!long.TryParse(rawId, out var userId) || userId <= 0)
        {
            return (false, null, await req.ErrorAsync(System.Net.HttpStatusCode.Unauthorized, "Invalid user id"));
        }

        var legacyUser = await userRepository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (legacyUser is null)
        {
            return (false, null, await req.ErrorAsync(System.Net.HttpStatusCode.Unauthorized, "User not registered"));
        }

        var authRecord = await userAuthRepository.GetAsync(legacyUser.Id, cancellationToken).ConfigureAwait(false);
        if (authRecord is null)
        {
            await userAuthRepository.EnsureExistsAsync(legacyUser.Id, cancellationToken).ConfigureAwait(false);
            authRecord = new UserAuthRecord { UserId = legacyUser.Id, MfaEnabled = false, MfaMethodsJson = null };
        }

        return (true, new AuthContext(legacyUser.Id, legacyUser.Email, legacyUser.DisplayName, authRecord.MfaEnabled), null);
    }

    private static bool TryGetBearer(HttpRequestData req, out string token)
    {
        token = string.Empty;
        if (req.Headers.TryGetValues("X-DocControl-Token", out var docControlValues))
        {
            token = docControlValues.FirstOrDefault()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(token))
            {
                return true;
            }
        }

        if (!req.Headers.TryGetValues("Authorization", out var values))
        {
            return false;
        }

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) return false;
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        token = raw[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    private static bool TryGetSwaPrincipal(HttpRequestData req, out ClientPrincipal principal)
    {
        principal = new ClientPrincipal();
        if (!req.Headers.TryGetValues("x-ms-client-principal", out var values))
        {
            return false;
        }

        var encoded = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            principal = JsonSerializer.Deserialize<ClientPrincipal>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ClientPrincipal();
            return !string.IsNullOrWhiteSpace(principal.UserDetails);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetClaimValue(ClientPrincipal principal, string type)
    {
        if (principal.Claims is null) return null;
        return principal.Claims.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}

internal sealed record ClientPrincipal(
    string? IdentityProvider = null,
    string? UserId = null,
    string? UserDetails = null,
    IReadOnlyList<ClientPrincipalClaim>? Claims = null);

internal sealed record ClientPrincipalClaim(
    [property: JsonPropertyName("typ")] string Type,
    [property: JsonPropertyName("val")] string Value);
