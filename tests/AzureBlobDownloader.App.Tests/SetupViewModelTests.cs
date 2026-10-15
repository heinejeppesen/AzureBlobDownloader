using AzureBlobDownloader.App.ViewModels;
using AzureBlobDownloader.Core.Auth;

namespace AzureBlobDownloader.App.Tests;

public class SetupViewModelTests
{
    private const string Tenant = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string Client = "5f4e3d2c-1b0a-4f9e-8d7c-6b5a4f3e2d1c";

    private const string Resolved = "9c8b7a6f-5e4d-4c3b-8a29-1f0e9d8c7b6a";

    private readonly List<(Guid Tenant, Guid Client)> _saved = [];
    private readonly List<string> _lookedUp = [];
    private bool _cancelled;

    /// <param name="check">What the lookup answers; an existing tenant resolves to the entered GUID, or to <see cref="Resolved"/> for a domain.</param>
    private SetupViewModel Create(string? tenant = null, string? client = null, bool firstRun = true, TenantCheckResult check = TenantCheckResult.Exists, Exception? saveFails = null) => new(
        tenant,
        client,
        firstRun,
        "C:\\Users\\x\\AppData\\Roaming\\AzureBlobDownloader\\AzureBlobDownloader.config.json",
        (t, c, _) =>
        {
            if (saveFails is not null)
            {
                throw saveFails;
            }

            _saved.Add((t, c));
            return Task.CompletedTask;
        },
        () => _cancelled = true,
        (input, _) =>
        {
            _lookedUp.Add(input);
            var id = Guid.TryParse(input, out var g) ? g : Guid.Parse(Resolved);
            return Task.FromResult(new TenantLookup(check, check == TenantCheckResult.Exists ? id : null));
        });

    [Theory]
    [InlineData(Tenant)]
    [InlineData("  " + Tenant + "  ")]
    [InlineData("{" + Tenant + "}")]
    [InlineData("0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D")]
    public void Common_ways_of_copying_a_guid_are_accepted(string text)
    {
        Assert.True(SetupViewModel.TryParseId(text, out var id));
        Assert.Equal(Guid.Parse(Tenant), id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d")]
    public void Invalid_ids_are_refused(string text)
    {
        Assert.False(SetupViewModel.TryParseId(text, out _));
    }

    [Fact]
    public void Save_is_enabled_only_when_both_ids_are_valid_and_errors_show_only_for_bad_input()
    {
        var setup = Create();
        Assert.False(setup.SaveCommand.CanExecute(null));
        Assert.Null(setup.TenantIdError);

        setup.TenantIdText = "nope";
        Assert.NotNull(setup.TenantIdError);

        setup.TenantIdText = Tenant;
        setup.ClientIdText = Client;
        Assert.Null(setup.TenantIdError);
        Assert.True(setup.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Placeholders_are_not_shown_as_values_and_known_values_are()
    {
        var setup = Create(tenant: "00000000-0000-0000-0000-000000000000", client: Client);

        Assert.Equal(string.Empty, setup.TenantIdText);
        Assert.Equal(Client, setup.ClientIdText);
    }

    [Fact]
    public async Task Saving_passes_the_parsed_ids()
    {
        var setup = Create(tenant: "{" + Tenant + "}", client: " " + Client);

        await setup.SaveCommand.ExecuteAsync(null);

        Assert.Equal([(Guid.Parse(Tenant), Guid.Parse(Client))], _saved);
        Assert.Null(setup.SaveError);
        Assert.False(setup.IsSaving);
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_saved()
    {
        var setup = Create(Tenant, Client, check: TenantCheckResult.NotFound);

        await setup.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_saved);
        Assert.Contains("No organisation", setup.SaveError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_tenant_cannot_be_checked_the_values_are_saved_anyway()
    {
        var setup = Create(Tenant, Client, check: TenantCheckResult.Unknown);

        await setup.SaveCommand.ExecuteAsync(null);

        Assert.Single(_saved);
    }

    [Theory]
    [InlineData("contoso.com")]
    [InlineData(" Contoso.com ")]
    [InlineData("someone@contoso.com")]
    public async Task A_domain_is_looked_up_and_its_tenant_id_is_saved(string input)
    {
        var setup = Create(input, Client);
        Assert.Null(setup.TenantIdError);
        Assert.True(setup.SaveCommand.CanExecute(null));

        await setup.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["contoso.com"], _lookedUp);
        Assert.Equal([(Guid.Parse(Resolved), Guid.Parse(Client))], _saved);
    }

    [Fact]
    public async Task An_unknown_domain_is_not_saved()
    {
        var setup = Create("contoso.example", Client, check: TenantCheckResult.NotFound);

        await setup.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_saved);
        Assert.Contains("\"contoso.example\"", setup.SaveError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_domain_that_cannot_be_looked_up_is_not_saved()
    {
        var setup = Create("contoso.com", Client, check: TenantCheckResult.Unknown);

        await setup.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_saved);
        Assert.Contains("could not be looked up", setup.SaveError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("contoso")]
    [InlineData("https://contoso.com")]
    public void Input_that_is_neither_a_domain_nor_a_tenant_id_is_refused(string input)
    {
        var setup = Create(input, Client);

        Assert.NotNull(setup.TenantIdError);
        Assert.False(setup.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_save_is_shown()
    {
        var setup = Create(Tenant, Client, saveFails: new UnauthorizedAccessException("Access denied"));

        await setup.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Access denied", setup.SaveError, StringComparison.Ordinal);
    }

    [Fact]
    public void First_run_cannot_be_cancelled_but_changing_settings_can()
    {
        var first = Create(firstRun: true);
        Assert.False(first.CanCancel);
        Assert.False(first.CancelCommand.CanExecute(null));

        var change = Create(Tenant, Client, firstRun: false);
        Assert.True(change.CanCancel);
        change.CancelCommand.Execute(null);
        Assert.True(_cancelled);
    }
}
