using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AzureBlobDownloader.Core.Auth;

public enum TenantCheckResult
{
    /// <summary>The tenant exists.</summary>
    Exists,

    /// <summary>Microsoft Entra ID does not know this tenant or domain.</summary>
    NotFound,

    /// <summary>Could not check (offline, proxy, service problem).</summary>
    Unknown,
}

/// <summary>The outcome of looking up a tenant; <see cref="TenantId"/> is set when it exists.</summary>
public sealed record TenantLookup(TenantCheckResult Result, Guid? TenantId)
{
    public static TenantLookup NotFound { get; } = new(TenantCheckResult.NotFound, null);

    public static TenantLookup Unknown { get; } = new(TenantCheckResult.Unknown, null);
}

/// <summary>
/// Looks up a tenant by Tenant ID or by one of its verified domains (e.g. <c>contoso.com</c>), using the
/// public sign-in metadata of the same sign-in service the app uses anyway. No credentials are sent.
/// </summary>
public static class TenantCheck
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Checks that the tenant exists.</summary>
    public static async Task<TenantCheckResult> CheckAsync(Guid tenantId, CancellationToken cancellationToken, HttpClient? client = null) =>
        (await LookupAsync(tenantId.ToString("D"), cancellationToken, client).ConfigureAwait(false)).Result;

    /// <summary>Finds the Tenant ID for a Tenant ID or domain accepted by <see cref="TryParseTenant"/>.</summary>
    public static async Task<TenantLookup> LookupAsync(string tenantIdOrDomain, CancellationToken cancellationToken, HttpClient? client = null)
    {
        if (!TryParseTenant(tenantIdOrDomain, out var tenant))
        {
            return TenantLookup.NotFound;
        }

        var url = $"{AuthService.LoginHost}/{tenant}/v2.0/.well-known/openid-configuration";
        try
        {
            using var response = await (client ?? SharedClient).GetAsync(url, cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.BadRequest or HttpStatusCode.NotFound:
                    return TenantLookup.NotFound;
                case not HttpStatusCode.OK:
                    return TenantLookup.Unknown;
            }

            // The issuer is "https://login.microsoftonline.com/<tenant id>/v2.0", also when asked by domain.
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("issuer", out var issuer)
                && Uri.TryCreate(issuer.GetString(), UriKind.Absolute, out var issuerUri)
                && issuerUri.Segments is [_, var idSegment, ..]
                && Guid.TryParse(idSegment.TrimEnd('/'), out var tenantId)
                && tenantId != Guid.Empty)
            {
                return new TenantLookup(TenantCheckResult.Exists, tenantId);
            }

            return TenantLookup.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return TenantLookup.Unknown;
        }
    }

    /// <summary>
    /// Accepts a Tenant ID (any usual GUID format) or a domain, also given as an e-mail address or with a
    /// leading <c>@</c>. Returns the normalised form: GUID "D" format, or the lowercase ASCII (punycode) domain.
    /// </summary>
    public static bool TryParseTenant(string? text, out string tenant)
    {
        tenant = string.Empty;
        var value = text?.Trim().Trim('{', '}', '"', '\'', '<', '>').Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (Guid.TryParse(value, out var guid))
        {
            if (guid == Guid.Empty)
            {
                return false;
            }

            tenant = guid.ToString("D");
            return true;
        }

        // "someone@contoso.com" or "@contoso.com" → "contoso.com"
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            value = value[(at + 1)..];
        }

        string domain;
        try
        {
            domain = new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!IsDomain(domain))
        {
            return false;
        }

        tenant = domain;
        return true;
    }

    /// <summary>At least two labels of letters, digits and hyphens (no leading/trailing hyphen), at most 253 characters.</summary>
    private static bool IsDomain(string value)
    {
        if (value.Length is 0 or > 253)
        {
            return false;
        }

        var labels = value.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is > 0 and <= 63
            && label[0] != '-'
            && label[^1] != '-'
            && label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));
    }
}
