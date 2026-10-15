using System.Net;
using System.Net.Http;
using AzureBlobDownloader.Core.Auth;

namespace AzureBlobDownloader.Core.Tests.Auth;

public class TenantCheckTests
{
    private static readonly Guid Tenant = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>What the sign-in service returns for an existing tenant (shortened).</summary>
    private static HttpResponseMessage Metadata(Guid tenant) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{ "issuer": "https://login.microsoftonline.com/{{tenant}}/v2.0", "token_endpoint": "https://login.microsoftonline.com/{{tenant}}/oauth2/v2.0/token" }"""),
    };

    private static async Task<(TenantLookup Lookup, Uri? Url)> Lookup(string input, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        var lookup = await TenantCheck.LookupAsync(input, TestContext.Current.CancellationToken, new HttpClient(handler));
        return (lookup, handler.LastRequest);
    }

    [Fact]
    public async Task Existing_tenant_is_reported_and_only_public_metadata_is_requested()
    {
        var handler = new Handler(_ => Metadata(Tenant));

        var result = await TenantCheck.CheckAsync(Tenant, TestContext.Current.CancellationToken, new HttpClient(handler));

        Assert.Equal(TenantCheckResult.Exists, result);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/v2.0/.well-known/openid-configuration", handler.LastRequest!.AbsoluteUri);
    }

    [Theory]
    [InlineData("contoso.com")]
    [InlineData("  Contoso.COM ")]
    [InlineData("someone@contoso.com")]
    [InlineData("@contoso.com")]
    [InlineData("contoso.com.")]
    public async Task A_domain_resolves_to_its_tenant_id(string input)
    {
        var (lookup, url) = await Lookup(input, _ => Metadata(Tenant));

        Assert.Equal(TenantCheckResult.Exists, lookup.Result);
        Assert.Equal(Tenant, lookup.TenantId);
        Assert.Equal("https://login.microsoftonline.com/contoso.com/v2.0/.well-known/openid-configuration", url!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)] // AADSTS90002: tenant not found
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Unknown_tenant_or_domain_is_reported(HttpStatusCode status)
    {
        Assert.Equal(TenantCheckResult.NotFound, (await Lookup("no-such-org.example", _ => new HttpResponseMessage(status))).Lookup.Result);
    }

    [Fact]
    public async Task Service_or_network_problems_are_unknown_not_a_verdict()
    {
        Assert.Equal(TenantCheckResult.Unknown, (await Lookup("contoso.com", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).Lookup.Result);
        Assert.Equal(TenantCheckResult.Unknown, (await Lookup("contoso.com", _ => throw new HttpRequestException("offline"))).Lookup.Result);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "issuer": "https://login.microsoftonline.com/common/v2.0" }""")]
    [InlineData("""{ "token_endpoint": "x" }""")]
    public async Task An_unexpected_answer_is_unknown(string body)
    {
        var (lookup, _) = await Lookup("contoso.com", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        Assert.Equal(TenantCheckResult.Unknown, lookup.Result);
        Assert.Null(lookup.TenantId);
    }

    [Fact]
    public async Task Invalid_input_is_not_sent_anywhere()
    {
        var (lookup, url) = await Lookup("contoso.com/../../evil", _ => Metadata(Tenant));

        Assert.Equal(TenantCheckResult.NotFound, lookup.Result);
        Assert.Null(url);
    }

    [Theory]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("{0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D}", "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("contoso.onmicrosoft.com", "contoso.onmicrosoft.com")]
    [InlineData("Ex.Example.CO.UK", "ex.example.co.uk")]
    [InlineData("first.last@contoso.com", "contoso.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    public void Tenant_input_is_normalised(string input, string expected)
    {
        Assert.True(TenantCheck.TryParseTenant(input, out var tenant));
        Assert.Equal(expected, tenant);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contoso")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("https://contoso.com")]
    [InlineData("contoso.com/path")]
    [InlineData("-contoso.com")]
    [InlineData("contoso..com")]
    [InlineData("conto so.com")]
    [InlineData("someone@")]
    public void Invalid_tenant_input_is_refused(string input)
    {
        Assert.False(TenantCheck.TryParseTenant(input, out _));
    }
}
