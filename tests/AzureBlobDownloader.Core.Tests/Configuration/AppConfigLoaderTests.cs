using AzureBlobDownloader.Core.Configuration;

namespace AzureBlobDownloader.Core.Tests.Configuration;

public sealed class AppConfigLoaderTests : IDisposable
{
    private const string Tenant = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string Client = "11111111-2222-3333-4444-555555555555";
    private const string OtherClient = "66666666-7777-8888-9999-aaaaaaaaaaaa";

    private readonly string _root;
    private readonly string _baseDir;
    private readonly string _userDir;

    public AppConfigLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureBlobDownloader.Tests", Guid.NewGuid().ToString("N"));
        _baseDir = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        _userDir = Path.Combine(_root, "user");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteBase(string json) => File.WriteAllText(Path.Combine(_baseDir, AppConfigLoader.FileName), json);

    private void WriteUser(string json)
    {
        Directory.CreateDirectory(_userDir);
        File.WriteAllText(Path.Combine(_userDir, AppConfigLoader.FileName), json);
    }

    private string WriteExplicit(string json, string name = "explicit.json")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, json);
        return path;
    }

    private AppConfig Load(string? explicitPath = null) => AppConfigLoader.Load(_baseDir, _userDir, explicitPath);

    private AppConfigException LoadFails(string? explicitPath = null) =>
        Assert.Throws<AppConfigException>(() => Load(explicitPath));

    // ---- Basic loading and defaults ----------------------------------------------------------

    [Fact]
    public void Minimal_config_gets_defaults()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" }""");

        var config = Load();

        Assert.Equal(Guid.Parse(Tenant), config.TenantId);
        Assert.Equal(Guid.Parse(Client), config.ClientId);
        Assert.Equal(new[] { "*.blob.core.windows.net" }, config.AllowedHosts);
        Assert.True(config.UseWindowsBroker);
        Assert.Null(config.DisplayName);
        Assert.Null(config.SupportContact);
        Assert.Equal(4, config.MaxConcurrentFiles);
        Assert.Equal(4, config.MaxConcurrentChunksPerFile);
        Assert.Equal(8, config.ChunkSizeMiB);
        Assert.Equal(8L * 1024 * 1024, config.ChunkSizeBytes);
    }

    [Fact]
    public void All_properties_are_read()
    {
        WriteBase($$"""
            {
              "TenantId": "{{Tenant}}",
              "ClientId": "{{Client}}",
              "AllowedHosts": [ "*.blob.core.windows.net", "CDA*.blob.core.windows.net" ],
              "UseWindowsBroker": false,
              "DisplayName": "  Contoso Downloads ",
              "SupportContact": "support@example.com",
              "MaxConcurrentFiles": 8,
              "MaxConcurrentChunksPerFile": 2,
              "ChunkSizeMiB": 16
            }
            """);

        var config = Load();

        Assert.Equal(new[] { "*.blob.core.windows.net", "cda*.blob.core.windows.net" }, config.AllowedHosts);
        Assert.False(config.UseWindowsBroker);
        Assert.Equal("Contoso Downloads", config.DisplayName);
        Assert.Equal("support@example.com", config.SupportContact);
        Assert.Equal(8, config.MaxConcurrentFiles);
        Assert.Equal(2, config.MaxConcurrentChunksPerFile);
        Assert.Equal(16, config.ChunkSizeMiB);
    }

    [Fact]
    public void Property_names_are_case_insensitive_and_comments_and_trailing_commas_are_allowed()
    {
        WriteBase($$"""
            {
              // resource tenant
              "tenantid": "{{Tenant}}",
              /* app registration */
              "CLIENTID": "{{Client}}",
              "allowedHosts": [ "cda*.blob.core.windows.net", ],
            }
            """);

        var config = Load();
        Assert.Equal(Guid.Parse(Client), config.ClientId);
    }

    [Fact]
    public void Schema_property_is_ignored()
    {
        WriteBase($$"""{ "$schema": "./config.schema.json", "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" }""");
        Assert.Equal(Guid.Parse(Tenant), Load().TenantId);
    }

    [Fact]
    public void Blank_optional_strings_become_null()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "DisplayName": "   ", "SupportContact": "" }""");
        var config = Load();
        Assert.Null(config.DisplayName);
        Assert.Null(config.SupportContact);
    }

    [Fact]
    public void Duplicate_allowed_hosts_are_collapsed()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "AllowedHosts": ["cda*.blob.core.windows.net", "CDA*.BLOB.CORE.WINDOWS.NET"] }""");
        Assert.Single(Load().AllowedHosts);
    }

    // ---- Layering ----------------------------------------------------------------------------

    [Fact]
    public void User_override_sets_missing_client_id_and_overrides_values()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "00000000-0000-0000-0000-000000000000", "MaxConcurrentFiles": 2 }""");
        WriteUser($$"""{ "ClientId": "{{Client}}", "MaxConcurrentFiles": 6 }""");

        var config = Load();

        Assert.Equal(Guid.Parse(Tenant), config.TenantId);
        Assert.Equal(Guid.Parse(Client), config.ClientId);
        Assert.Equal(6, config.MaxConcurrentFiles);
    }

    [Fact]
    public void Unset_properties_in_override_keep_base_values()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "UseWindowsBroker": false, "DisplayName": "Base" }""");
        WriteUser("""{ "ChunkSizeMiB": 32 }""");

        var config = Load();

        Assert.False(config.UseWindowsBroker);
        Assert.Equal("Base", config.DisplayName);
        Assert.Equal(32, config.ChunkSizeMiB);
    }

    [Fact]
    public void Override_list_replaces_base_list()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "AllowedHosts": ["cda*.blob.core.windows.net"] }""");
        WriteUser("""{ "AllowedHosts": ["cdatest*.blob.core.windows.net"] }""");

        Assert.Equal(new[] { "cdatest*.blob.core.windows.net" }, Load().AllowedHosts);
    }

    [Fact]
    public void Explicit_config_overrides_user_and_base()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "MaxConcurrentFiles": 2 }""");
        WriteUser("""{ "MaxConcurrentFiles": 3, "ChunkSizeMiB": 4 }""");
        var explicitPath = WriteExplicit($$"""{ "ClientId": "{{OtherClient}}", "MaxConcurrentFiles": 5 }""");

        var config = Load(explicitPath);

        Assert.Equal(Guid.Parse(OtherClient), config.ClientId);
        Assert.Equal(5, config.MaxConcurrentFiles);
        Assert.Equal(4, config.ChunkSizeMiB);
    }

    [Fact]
    public void Explicit_config_alone_is_enough()
    {
        var explicitPath = WriteExplicit($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" }""");
        var config = AppConfigLoader.Load(_baseDir, userConfigDirectory: null, explicitPath);
        Assert.Equal(Guid.Parse(Client), config.ClientId);
    }

    [Fact]
    public void User_config_alone_is_enough()
    {
        WriteUser($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" }""");
        Assert.Equal(Guid.Parse(Client), Load().ClientId);
    }

    [Fact]
    public void Missing_explicit_config_is_an_error()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" }""");
        var missing = Path.Combine(_root, "does-not-exist.json");

        var ex = LoadFails(missing);

        Assert.Contains(ex.Errors, e => e.Contains("--config", StringComparison.Ordinal) && e.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public void No_config_files_at_all_is_an_error()
    {
        var ex = LoadFails();
        Assert.Contains("No configuration file was found", Assert.Single(ex.Errors), StringComparison.Ordinal);
        Assert.Empty(ex.Sources);
    }

    [Fact]
    public void Sources_are_reported_in_merge_order()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}" }""");
        WriteUser("""{ "MaxConcurrentFiles": 3 }""");

        var ex = LoadFails();

        Assert.Equal(2, ex.Sources.Count);
        Assert.StartsWith(_baseDir, ex.Sources[0], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_userDir, ex.Sources[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ex.Sources[0], ex.Message, StringComparison.Ordinal);
    }

    // ---- Validation --------------------------------------------------------------------------

    [Fact]
    public void Missing_required_fields_are_all_listed()
    {
        WriteBase("""{ "UseWindowsBroker": true }""");

        var ex = LoadFails();

        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.StartsWith("TenantId is missing", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("ClientId is missing", StringComparison.Ordinal));
        Assert.Contains("TenantId is missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ClientId is missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void All_zero_client_id_is_reported_as_not_configured()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "00000000-0000-0000-0000-000000000000" }""");

        var ex = LoadFails();

        var error = Assert.Single(ex.Errors);
        Assert.StartsWith("ClientId is not configured yet", error, StringComparison.Ordinal);
        Assert.Contains("placeholder", error, StringComparison.Ordinal);
    }

    [Fact]
    public void All_zero_tenant_id_is_reported_as_not_configured()
    {
        WriteBase($$"""{ "TenantId": "00000000-0000-0000-0000-000000000000", "ClientId": "{{Client}}" }""");
        Assert.StartsWith("TenantId is not configured yet", Assert.Single(LoadFails().Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d")]
    [InlineData("{0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d}")]
    [InlineData("0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d")]
    public void Invalid_guid_is_reported(string value)
    {
        WriteBase($$"""{ "TenantId": "{{value}}", "ClientId": "{{Client}}" }""");
        var error = Assert.Single(LoadFails().Errors);
        Assert.StartsWith("TenantId", error, StringComparison.Ordinal);
        Assert.Contains("not a valid GUID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Guid_with_surrounding_whitespace_is_accepted()
    {
        WriteBase($$"""{ "TenantId": " {{Tenant}} ", "ClientId": "{{Client.ToUpperInvariant()}}" }""");
        Assert.Equal(Guid.Parse(Client), Load().ClientId);
    }

    [Fact]
    public void Empty_allowed_hosts_is_an_error()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "AllowedHosts": [] }""");
        Assert.Contains("AllowedHosts is empty", Assert.Single(LoadFails().Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_allowed_host_patterns_are_each_reported()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "AllowedHosts": ["*", "cda*.blob.core.windows.net", "https://evil.com"] }""");

        var ex = LoadFails();

        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.Contains("\"*\"", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("\"https://evil.com\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MaxConcurrentFiles", 0)]
    [InlineData("MaxConcurrentFiles", 65)]
    [InlineData("MaxConcurrentChunksPerFile", -1)]
    [InlineData("MaxConcurrentChunksPerFile", 1000)]
    [InlineData("ChunkSizeMiB", 0)]
    [InlineData("ChunkSizeMiB", 257)]
    public void Out_of_range_numbers_are_reported(string property, int value)
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "{{property}}": {{value}} }""");
        var error = Assert.Single(LoadFails().Errors);
        Assert.StartsWith($"{property} is {value}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_settings_are_reported_together_before_missing_ids()
    {
        WriteBase("""{ "ClientId": "00000000-0000-0000-0000-000000000000", "MaxConcurrentFiles": 0, "AllowedHosts": ["*"] }""");

        // The IDs can be entered on the setup screen; the invalid settings cannot, so they are the errors.
        Assert.Equal(2, LoadFails().Errors.Count);
    }

    // ---- Malformed files ---------------------------------------------------------------------

    [Fact]
    public void Invalid_json_is_reported_with_the_file_path()
    {
        WriteBase("""{ "TenantId": """);

        var error = Assert.Single(LoadFails().Errors);

        Assert.Contains(Path.Combine(_baseDir, AppConfigLoader.FileName), error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is not valid", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_value_type_is_reported()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "MaxConcurrentFiles": "four" }""");
        Assert.Contains("is not valid", Assert.Single(LoadFails().Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_property_is_reported()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "AllowedHost": ["x"] }""");
        Assert.Contains("AllowedHost", Assert.Single(LoadFails().Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_null_document_is_reported_as_empty()
    {
        WriteBase("null");
        Assert.Contains("is empty", Assert.Single(LoadFails().Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Error_in_override_file_is_reported_even_if_base_is_valid()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" }""");
        WriteUser("{ not json");

        var error = Assert.Single(LoadFails().Errors);
        Assert.Contains(Path.Combine(_userDir, AppConfigLoader.FileName), error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Shipped config file -----------------------------------------------------------------

    private static string ShippedDir => Path.Combine(AppContext.BaseDirectory, "Shipped");

    [Fact]
    public void Shipped_config_has_no_ids_and_asks_for_setup()
    {
        Assert.True(File.Exists(Path.Combine(ShippedDir, AppConfigLoader.FileName)), "Shipped config not copied to test output.");

        var result = AppConfigLoader.TryLoad(new AppConfigLocations(ShippedDir, null, null, null));

        Assert.True(result.NeedsSetup);
        Assert.Equal(2, result.Missing.Count);
        Assert.True(string.IsNullOrEmpty(result.TenantId));
        Assert.True(string.IsNullOrEmpty(result.ClientId));
    }

    [Fact]
    public void Shipped_config_works_once_the_user_has_entered_the_ids()
    {
        UserConfigWriter.SaveIdentity(_userDir, Guid.Parse(Tenant), Guid.Parse(Client));

        var result = AppConfigLoader.TryLoad(new AppConfigLocations(ShippedDir, null, _userDir, null));

        Assert.False(result.NeedsSetup);
        Assert.Equal(Guid.Parse(Tenant), result.Config!.TenantId);
        Assert.Equal(Guid.Parse(Client), result.Config.ClientId);
        Assert.Equal(["*.blob.core.windows.net"], result.Config.AllowedHosts);
        Assert.True(result.Config.UseWindowsBroker);
    }

    // ---- Setup (missing IDs) -----------------------------------------------------------------

    private string MachineDir => Path.Combine(_root, "machine");

    private void WriteMachine(string json)
    {
        Directory.CreateDirectory(MachineDir);
        File.WriteAllText(Path.Combine(MachineDir, AppConfigLoader.FileName), json);
    }

    private AppConfigLoadResult TryLoad(string? explicitPath = null) =>
        AppConfigLoader.TryLoad(new AppConfigLocations(_baseDir, MachineDir, _userDir, explicitPath));

    [Fact]
    public void No_files_at_all_asks_for_setup()
    {
        var result = TryLoad();

        Assert.True(result.NeedsSetup);
        Assert.Empty(result.Sources);
    }

    [Theory]
    [InlineData("""{ "TenantId": "", "ClientId": "" }""")]
    [InlineData("""{ "TenantId": "   " }""")]
    [InlineData("""{ "TenantId": "00000000-0000-0000-0000-000000000000", "ClientId": "00000000-0000-0000-0000-000000000000" }""")]
    public void Blank_or_placeholder_ids_ask_for_setup(string json)
    {
        WriteBase(json);

        Assert.True(TryLoad().NeedsSetup);
    }

    [Fact]
    public void A_partial_setup_reports_what_is_already_known()
    {
        WriteBase($$"""{ "TenantId": " {{Tenant}} ", "ClientId": "" }""");

        var result = TryLoad();

        Assert.True(result.NeedsSetup);
        Assert.Equal(Tenant, result.TenantId);
        Assert.StartsWith("ClientId is missing", Assert.Single(result.Missing), StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_id_is_an_error_not_a_setup()
    {
        WriteBase($$"""{ "TenantId": "not-a-guid", "ClientId": "{{Client}}" }""");

        Assert.Throws<AppConfigException>(() => TryLoad());
    }

    [Fact]
    public void Invalid_settings_are_errors_even_when_setup_is_needed()
    {
        WriteBase("""{ "TenantId": "", "MaxConcurrentFiles": 0 }""");

        Assert.Throws<AppConfigException>(() => TryLoad());
    }

    [Fact]
    public void Blank_values_in_a_later_layer_do_not_hide_earlier_values()
    {
        WriteMachine($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "DisplayName": "Contoso" }""");
        WriteUser("""{ "TenantId": "", "ClientId": " ", "DisplayName": "" }""");

        var config = TryLoad().Config!;

        Assert.Equal(Guid.Parse(Tenant), config.TenantId);
        Assert.Equal("Contoso", config.DisplayName);
    }

    // ---- Machine-wide layer ------------------------------------------------------------------

    [Fact]
    public void Machine_file_overrides_shipped_and_user_file_overrides_machine()
    {
        WriteBase("""{ "TenantId": "", "ClientId": "", "MaxConcurrentFiles": 2, "ChunkSizeMiB": 4, "DisplayName": "Shipped" }""");
        WriteMachine($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "MaxConcurrentFiles": 3, "DisplayName": "Machine" }""");
        WriteUser($$"""{ "ClientId": "{{OtherClient}}", "MaxConcurrentFiles": 5 }""");

        var result = TryLoad();

        Assert.Equal(Guid.Parse(Tenant), result.Config!.TenantId);       // machine
        Assert.Equal(Guid.Parse(OtherClient), result.Config.ClientId);    // user
        Assert.Equal(5, result.Config.MaxConcurrentFiles);                // user
        Assert.Equal(4, result.Config.ChunkSizeMiB);                      // shipped
        Assert.Equal("Machine", result.Config.DisplayName);               // machine
        Assert.Equal(3, result.Sources.Count);
        Assert.StartsWith(MachineDir, result.Sources[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Machine_file_alone_is_enough()
    {
        WriteMachine($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "AllowedHosts": ["cda*.blob.core.windows.net"] }""");

        var config = TryLoad().Config!;

        Assert.Equal(["cda*.blob.core.windows.net"], config.AllowedHosts);
    }

    [Fact]
    public void Machine_config_directory_is_per_os()
    {
        var dir = AppConfigLoader.GetMachineConfigDirectory();

        Assert.NotNull(dir);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AzureBlobDownloader"), dir);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("/Library/Application Support/AzureBlobDownloader", dir);
        }
        else
        {
            Assert.Equal("/etc/AzureBlobDownloader", dir);
        }
    }

    // ---- Allowed hosts must be Azure blob endpoints ------------------------------------------

    [Theory]
    [InlineData("*.example.com")]
    [InlineData("cda*.blob.core.windows.net.example.com")]
    [InlineData("*.core.windows.net")]
    [InlineData("*.file.core.windows.net")]
    [InlineData("*.blob.storage.azure.net")]
    public void Allowed_hosts_outside_azure_blob_storage_are_refused(string pattern)
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}", "AllowedHosts": ["{{pattern}}"] }""");

        Assert.Contains("only Azure blob endpoints", Assert.Single(LoadFails().Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Allowed_hosts_rule_applies_to_every_layer()
    {
        WriteBase($$"""{ "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" }""");
        WriteUser("""{ "AllowedHosts": ["*.evil.example"] }""");

        Assert.Throws<AppConfigException>(() => TryLoad());
    }

    // ---- Saving the setup values -------------------------------------------------------------

    private string UserFile => Path.Combine(_userDir, AppConfigLoader.FileName);

    [Fact]
    public void Saving_creates_the_folder_and_the_file()
    {
        var path = UserConfigWriter.SaveIdentity(_userDir, Guid.Parse(Tenant), Guid.Parse(Client));

        Assert.Equal(UserFile, path);
        var config = TryLoad().Config!;
        Assert.Equal(Guid.Parse(Tenant), config.TenantId);
        Assert.Equal(Guid.Parse(Client), config.ClientId);
        Assert.False(File.Exists(UserFile + ".tmp"));
    }

    [Fact]
    public void Saving_keeps_other_settings_and_replaces_any_spelling_of_the_ids()
    {
        WriteUser($$"""
            {
              // my settings
              "tenantid": "{{OtherClient}}",
              "MaxConcurrentFiles": 7,
              "DisplayName": "Mine",
            }
            """);

        UserConfigWriter.SaveIdentity(_userDir, Guid.Parse(Tenant), Guid.Parse(Client));

        var text = File.ReadAllText(UserFile);
        Assert.DoesNotContain("tenantid", text, StringComparison.Ordinal);
        var config = TryLoad().Config!;
        Assert.Equal(Guid.Parse(Tenant), config.TenantId);
        Assert.Equal(7, config.MaxConcurrentFiles);
        Assert.Equal("Mine", config.DisplayName);
    }

    [Fact]
    public void Saving_over_a_damaged_file_keeps_a_backup()
    {
        WriteUser("{ not json");

        UserConfigWriter.SaveIdentity(_userDir, Guid.Parse(Tenant), Guid.Parse(Client));

        Assert.Equal("{ not json", File.ReadAllText(UserFile + ".bak"));
        Assert.Equal(Guid.Parse(Client), TryLoad().Config!.ClientId);
    }

    [Fact]
    public void Saving_empty_ids_is_refused()
    {
        Assert.Throws<ArgumentException>(() => UserConfigWriter.SaveIdentity(_userDir, Guid.Empty, Guid.Parse(Client)));
        Assert.False(File.Exists(UserFile));
    }

    // ---- Command line ------------------------------------------------------------------------

    [Fact]
    public void No_config_argument_returns_null()
    {
        Assert.Null(AppConfigLoader.GetConfigPathFromArgs([]));
        Assert.Null(AppConfigLoader.GetConfigPathFromArgs(["--verbose", "https://cda1.blob.core.windows.net/abc"]));
    }

    [Theory]
    [InlineData(new[] { "--config", "C:\\cfg\\a.json" }, "C:\\cfg\\a.json")]
    [InlineData(new[] { "--CONFIG", "a.json" }, "a.json")]
    [InlineData(new[] { "--config=a.json" }, "a.json")]
    [InlineData(new[] { "--config=\"C:\\My Folder\\a.json\"" }, "C:\\My Folder\\a.json")]
    [InlineData(new[] { "x", "--config", "a.json", "y" }, "a.json")]
    [InlineData(new[] { "--config", "a.json", "--config", "b.json" }, "b.json")]
    public void Config_argument_is_parsed(string[] args, string expected)
    {
        Assert.Equal(expected, AppConfigLoader.GetConfigPathFromArgs(args));
    }

    [Theory]
    [InlineData(new object[] { new[] { "--config" } })]
    [InlineData(new object[] { new[] { "--config", "" } })]
    [InlineData(new object[] { new[] { "--config=" } })]
    public void Config_argument_without_value_is_an_error(string[] args)
    {
        var ex = Assert.Throws<AppConfigException>(() => AppConfigLoader.GetConfigPathFromArgs(args));
        Assert.Contains("--config", Assert.Single(ex.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void User_config_directory_is_per_os()
    {
        var dir = AppConfigLoader.GetUserConfigDirectory();

        Assert.NotNull(dir);
        Assert.Equal(AppConfigLoader.AppFolderName, Path.GetFileName(dir));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzureBlobDownloader"),
                dir);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.EndsWith("/Library/Application Support/AzureBlobDownloader", dir, StringComparison.Ordinal);
        }
    }
}
