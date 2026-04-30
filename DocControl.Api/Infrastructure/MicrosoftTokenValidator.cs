using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DocControl.Api.Infrastructure;

public sealed record MicrosoftDeviceCodeOptions(string ClientId, string TenantId, string Scopes);

public sealed record MicrosoftValidatedUser(string Email, string DisplayName, string Subject, string TenantId);

public sealed class MicrosoftTokenValidator
{
    private static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromHours(12);
    private readonly IConfiguration configuration;
    private readonly ILogger<MicrosoftTokenValidator> logger;
    private readonly JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
    private readonly SemaphoreSlim metadataLock = new(1, 1);
    private ConfigurationManager<OpenIdConnectConfiguration>? configurationManager;
    private string? configurationTenant;

    public MicrosoftTokenValidator(IConfiguration configuration, ILogger<MicrosoftTokenValidator> logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    public MicrosoftDeviceCodeOptions? GetDeviceCodeOptions()
    {
        var clientId = configuration["MicrosoftAuth:ClientId"] ?? configuration["MICROSOFT_AUTH_CLIENT_ID"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var tenantId = configuration["MicrosoftAuth:TenantId"] ?? configuration["MICROSOFT_AUTH_TENANT_ID"] ?? "common";
        var scopes = configuration["MicrosoftAuth:DeviceCodeScopes"] ?? configuration["MICROSOFT_AUTH_DEVICE_CODE_SCOPES"] ?? "openid profile email";
        return new MicrosoftDeviceCodeOptions(clientId.Trim(), tenantId.Trim(), scopes.Trim());
    }

    public async Task<MicrosoftValidatedUser?> ValidateIdTokenAsync(string idToken, CancellationToken cancellationToken)
    {
        var options = GetDeviceCodeOptions();
        if (options is null || string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        try
        {
            var openIdConfig = await GetOpenIdConfigurationAsync(options.TenantId, cancellationToken).ConfigureAwait(false);
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                IssuerValidator = (issuer, token, _) => ValidateIssuer(issuer, token, options.TenantId),
                ValidateAudience = true,
                ValidAudience = options.ClientId,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = openIdConfig.SigningKeys
            };

            var principal = handler.ValidateToken(idToken, validationParameters, out _);
            var email = FirstClaim(principal, "email", "preferred_username", "upn");
            var subject = FirstClaim(principal, "sub", ClaimTypes.NameIdentifier);
            var tenantId = FirstClaim(principal, "tid") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(subject))
            {
                return null;
            }

            var displayName = FirstClaim(principal, "name") ?? email;
            return new MicrosoftValidatedUser(email.Trim(), displayName.Trim(), subject.Trim(), tenantId.Trim());
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning(ex, "Rejected Microsoft CLI token");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to validate Microsoft CLI token");
            return null;
        }
    }

    private async Task<OpenIdConnectConfiguration> GetOpenIdConfigurationAsync(string tenantId, CancellationToken cancellationToken)
    {
        await metadataLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (configurationManager is null || !string.Equals(configurationTenant, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                var metadataAddress = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/v2.0/.well-known/openid-configuration";
                configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    metadataAddress,
                    new OpenIdConnectConfigurationRetriever())
                {
                    AutomaticRefreshInterval = MetadataRefreshInterval
                };
                configurationTenant = tenantId;
            }
        }
        finally
        {
            metadataLock.Release();
        }

        return await configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateIssuer(string issuer, SecurityToken token, string configuredTenantId)
    {
        var jwt = token as JwtSecurityToken;
        var tokenTenantId = jwt?.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
        if (string.IsNullOrWhiteSpace(tokenTenantId))
        {
            throw new SecurityTokenInvalidIssuerException("Missing Microsoft tenant id.");
        }

        if (!IsMultiTenant(configuredTenantId) &&
            !string.Equals(tokenTenantId, configuredTenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityTokenInvalidIssuerException("Unexpected Microsoft tenant id.");
        }

        var expectedIssuer = $"https://login.microsoftonline.com/{tokenTenantId}/v2.0";
        if (!string.Equals(issuer.TrimEnd('/'), expectedIssuer, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityTokenInvalidIssuerException("Unexpected Microsoft token issuer.");
        }

        return issuer;
    }

    private static bool IsMultiTenant(string tenantId)
    {
        return string.Equals(tenantId, "common", StringComparison.OrdinalIgnoreCase)
               || string.Equals(tenantId, "organizations", StringComparison.OrdinalIgnoreCase)
               || string.Equals(tenantId, "consumers", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstClaim(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.Claims.FirstOrDefault(c => string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
