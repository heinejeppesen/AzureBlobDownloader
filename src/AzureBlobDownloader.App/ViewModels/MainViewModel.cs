using System.ComponentModel;
using AzureBlobDownloader.App.Services;
using AzureBlobDownloader.Core.Auth;
using AzureBlobDownloader.Core.Configuration;
using AzureBlobDownloader.Core.Diagnostics;
using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Errors;
using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBlobDownloader.App.ViewModels;

/// <summary>
/// Main window: paste a link, sign in against the resource tenant, browse and tick what to download in
/// <see cref="Tree"/>, then download it (<see cref="Download"/>).
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public const string DefaultTitle = "Azure Blob Downloader";

    private readonly AppConfigLocations? _locations;
    private readonly UserSettings _settings = UserSettings.Load();
    private AppConfig? _config;
    private BlobUrlParser? _parser;
    private AuthService? _auth;
    private BlobBrowser? _browser;
    private AzureBlobReader? _reader;
    private CancellationTokenSource? _operation;
    private string? _clipboardText;

    /// <summary>Design-time constructor.</summary>
    public MainViewModel()
        : this(locations: null, load: null, configError: null)
    {
    }

    /// <param name="locations">Where the configuration is read from and saved to.</param>
    /// <param name="load">The loaded configuration, or the information that setup is needed.</param>
    /// <param name="configError">Why the configuration could not be loaded (invalid file).</param>
    public MainViewModel(AppConfigLocations? locations, AppConfigLoadResult? load, string? configError)
    {
        _locations = locations;
        Title = DefaultTitle;
        ConfigError = configError;
        StatusText = configError is null ? "Paste the link you received, then sign in." : "The app cannot start because its configuration is invalid.";

        Tree = new BrowseTreeViewModel(
            () => _browser ?? throw new InvalidOperationException("Not initialised."),
            HandleError);
        Tree.PropertyChanged += OnTreeChanged;

        if (load?.Config is { } config)
        {
            ApplyConfig(config);
        }
        else if (load is { NeedsSetup: true })
        {
            StatusText = "Enter the two values you received to set up the app.";
            Setup = CreateSetup(load.TenantId, load.ClientId, isFirstRun: true);
        }
    }

    /// <summary>The setup screen (Tenant ID and Client ID), shown at first start or from Change settings.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeSettingsCommand))]
    public partial SetupViewModel? Setup { get; set; }

    /// <summary>Tenant and client in use, for the connection details.</summary>
    [ObservableProperty]
    public partial string? SettingsText { get; set; }

    /// <summary>The sign-in error points at wrong settings (unknown tenant or app): offer Change settings.</summary>
    public bool ErrorSuggestsSettings => Error?.Code is "AADSTS700016" or "AADSTS90002" or "AADSTS90023";

    /// <summary>Native handle of the main window, used to parent the Windows sign-in dialog.</summary>
    public Func<IntPtr>? ParentWindowHandle { get; set; }

    /// <summary>Folder picker and other dialogs, provided by the window.</summary>
    public IAppDialogs? Dialogs { get; set; }

    public BrowseTreeViewModel Tree { get; }

    /// <summary>The running or finished download; null while browsing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsing))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(SignOutCommand), nameof(SwitchAccountCommand), nameof(RefreshCommand), nameof(DownloadCommand))]
    public partial DownloadViewModel? Download { get; set; }

    /// <summary>The pre-flight report waiting for the user's decision, if any.</summary>
    [ObservableProperty]
    public partial PreflightViewModel? Preflight { get; set; }

    public bool IsBrowsing => Download is null;

    public bool HasConfigError => ConfigError is not null;

    public bool IsSignedIn => User is not null;

    public string UserText => User is null ? "Not signed in" : User.UserName;

    public string? UserDetailText => User is null
        ? null
        : $"{(User.IsGuest ? "Guest" : "Member")} · object id {User.ObjectId} · token tenant {User.TokenTenantId}"
          + (User.IsGuest ? $" · home tenant {User.HomeTenantId}" : string.Empty);

    public bool HasError => Error is not null;

    public string? ErrorCodeText => Error?.Code is { } code ? $"Code: {code}" : null;

    public string? SupportText => _config?.SupportContact;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigError))]
    public partial string? ConfigError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string LinkText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LinkMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(UserText), nameof(UserDetailText))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand), nameof(SwitchAccountCommand), nameof(RefreshCommand))]
    public partial SignedInUser? User { get; set; }

    [ObservableProperty]
    public partial string? SignInMethodText { get; set; }

    [ObservableProperty]
    public partial string? CacheText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ErrorCodeText), nameof(ErrorSuggestsSettings))]
    public partial UserError? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(SignOutCommand), nameof(SwitchAccountCommand), nameof(RefreshCommand), nameof(CancelCommand), nameof(DownloadCommand), nameof(ChangeSettingsCommand))]
    public partial bool IsBusy { get; set; }

    private bool CanChangeSettings() => !IsBusy && Download is null && Setup is null && _locations?.UserDirectory is not null;

    /// <summary>Opens the setup screen to change the Tenant ID and Client ID.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSettings))]
    private void ChangeSettings() =>
        Setup = CreateSetup(_config?.TenantId.ToString("D"), _config?.ClientId.ToString("D"), isFirstRun: _config is null);

    private SetupViewModel CreateSetup(string? tenantId, string? clientId, bool isFirstRun) => new(
        tenantId,
        clientId,
        isFirstRun,
        _locations?.UserConfigPath,
        SaveSettingsAsync,
        () => Setup = null);

    /// <summary>
    /// Saves the values in the per-user config file, reloads the configuration and starts over with it:
    /// signed out, no link open.
    /// </summary>
    private async Task SaveSettingsAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        if (_locations?.UserDirectory is not { } userDirectory)
        {
            throw new InvalidOperationException("There is no folder to save settings in.");
        }

        var path = UserConfigWriter.SaveIdentity(userDirectory, tenantId, clientId);
        AppLog.Info($"Settings saved to {path}: tenant {tenantId}, client {clientId}.");

        var result = AppConfigLoader.TryLoad(_locations);
        if (result.Config is not { } config || config.TenantId != tenantId || config.ClientId != clientId)
        {
            throw new InvalidOperationException(
                $"The values were saved, but another configuration file overrides them: {result.Sources.LastOrDefault()}");
        }

        Tree.Close();
        if (_auth is not null)
        {
            await _auth.SignOutAsync();
        }

        _auth = null;
        _browser = null;
        _reader = null;
        User = null;
        Error = null;
        ConfigError = null;
        ApplyConfig(config);
        Setup = null;

        await InitializeAsync();
        OfferClipboardText(_clipboardText);
    }

    private void ApplyConfig(AppConfig config)
    {
        _config = config;
        _parser = new BlobUrlParser(config);
        Title = string.IsNullOrWhiteSpace(config.DisplayName) ? DefaultTitle : config.DisplayName;
        SettingsText = $"Tenant ID {config.TenantId} · Client ID {config.ClientId}"
                       + (_locations?.UserConfigPath is { } path ? $" · settings file {path}" : string.Empty);
        OnPropertyChanged(nameof(SupportText));
        OnLinkTextChanged(LinkText);
        OpenCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Creates the sign-in service and restores a remembered sign-in. Call once the window is open.</summary>
    public async Task InitializeAsync()
    {
        if (_config is null || _auth is not null)
        {
            return;
        }

        try
        {
            _auth = await AuthService.CreateAsync(_config, ParentWindowHandle);
            var credential = new StorageTokenCredential(_auth);
            _browser = new BlobBrowser(credential);
            _reader = new AzureBlobReader(credential);
            SignInMethodText = _auth.UsesBroker
                ? $"Sign-in: Windows account broker (WAM) · {_auth.Authority}"
                : $"Sign-in: system browser · {_auth.Authority}";
            CacheText = _auth.Cache.Description;

            IsBusy = true;
            StatusText = "Checking for a remembered sign-in…";
            User = await _auth.TryRestoreAsync(CancellationToken.None);
            StatusText = User is null ? "Paste the link you received, then sign in." : "Signed in. Paste the link you received.";
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Pre-fills the link box from the clipboard when it holds an allowed link, showing the clean link
    /// (not a whole copied e-mail paragraph or an Outlook Safe Links address).
    /// </summary>
    public void OfferClipboardText(string? text)
    {
        _clipboardText = text;
        if (string.IsNullOrEmpty(LinkText) && _parser is not null && _parser.TryParse(text, out var target))
        {
            LinkText = target.ToString();
        }
    }

    partial void OnLinkTextChanged(string value)
    {
        if (_parser is null || string.IsNullOrWhiteSpace(value))
        {
            LinkMessage = null;
            return;
        }

        var result = _parser.Parse(value);
        LinkMessage = result.IsSuccess
            ? result.SasRemoved ? "The access key (SAS) in this link will be ignored; the app uses your own sign-in." : null
            : result.ErrorMessage;
    }

    private bool CanOpen() => !IsBusy && Download is null && _auth is not null && _parser is not null && _parser.TryParse(LinkText, out _);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => RunAsync("Opening…", async ct =>
    {
        // Validate before signing in, so a pasted link can never trigger a sign-in for a foreign host.
        if (_parser is null || !_parser.TryParse(LinkText, out var target))
        {
            return;
        }

        if (User is null)
        {
            await SignInAsync(ct);
        }

        await OpenTargetAsync(target, ct);
    });

    private bool CanSignOut() => !IsBusy && Download is null && User is not null;

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private Task SignOutAsync() => RunAsync("Signing out…", async _ =>
    {
        Tree.Close();
        RefreshCommand.NotifyCanExecuteChanged();
        await _auth!.SignOutAsync();
        User = null;
        StatusText = "Signed out.";
    });

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private Task SwitchAccountAsync() => RunAsync("Switching account…", async ct =>
    {
        var link = Tree.Link;
        Tree.Close();
        await _auth!.SignOutAsync();
        User = null;
        await SignInAsync(ct);
        if (link is not null)
        {
            await OpenTargetAsync(link, ct);
        }
    });

    private bool CanRefresh() => !IsBusy && Download is null && Tree.Link is not null && User is not null;

    /// <summary>Lists the shared folder again from scratch (the selection is cleared).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunAsync("Loading…", ct => OpenTargetAsync(Tree.Link!, ct));

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _operation?.Cancel();

    private bool CanDownload() => !IsBusy && Download is null && User is not null && Tree.HasSelection && _reader is not null;

    /// <summary>Choose a folder, list everything selected, check the destination, then download.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (Dialogs is null || Tree.Link is not { } link)
        {
            return;
        }

        var folder = await Dialogs.PickFolderAsync("Choose where to save the files", _settings.LastDestination);
        if (folder is null)
        {
            return;
        }

        _settings.LastDestination = folder;
        _settings.Save();

        await RunAsync("Preparing the download…", async ct =>
        {
            var caseSensitive = await Task.Run(() => LocalDestination.ProbeCaseSensitivity(folder), ct);
            var progress = new Progress<int>(n => StatusText = $"Preparing the download… {n:N0} files found.");
            var plan = await DownloadPlanner.PlanAsync(
                Tree.GetSelection(), link, _reader!, new PlanOptions(LocalNames.Current, caseSensitive), progress, ct);

            StatusText = $"Checking {folder}…";
            var report = await Task.Run(() => Core.Downloads.Preflight.Check(plan, folder, LocalNames.Current, new LocalDestination()), ct);
            AppLog.Info($"Pre-flight: {report.ToDownload.Count} files, {report.BytesNeeded} bytes needed, {report.Issues.Count} issues, "
                        + $"{report.AlreadyThere} already there, blocking: {report.BlockingProblem is not null}.");

            if (report.NeedsAttention || !report.CanStart)
            {
                var preflight = new PreflightViewModel(report);
                Preflight = preflight;
                StatusText = "Check the details before the download starts.";
                bool start;
                try
                {
                    start = await preflight.Decision.WaitAsync(ct);
                }
                finally
                {
                    Preflight = null;
                }

                if (!start)
                {
                    StatusText = "The download was not started.";
                    return;
                }
            }

            StartDownload(report);
        });
    }

    private void StartDownload(PreflightReport report)
    {
        var options = new DownloadOptions
        {
            MaxConcurrentFiles = _config!.MaxConcurrentFiles,
            MaxConcurrentChunksPerFile = _config.MaxConcurrentChunksPerFile,
            ChunkSize = (int)_config.ChunkSizeBytes,
        };
        var job = new DownloadJob(report.ToDownload, report.Destination, _reader!, options, ReauthenticateForDownloadAsync);
        var download = new DownloadViewModel(job, report.AlreadyThere, Dialogs!, ct => _auth!.ReauthenticateAsync(ct));
        download.Closed += () =>
        {
            download.Dispose();
            Download = null;
            StatusText = "Tick the files and folders you want to download.";
        };

        Download = download;
        StatusText = string.Empty;
        AppLog.Info($"Download started: {report.ToDownload.Count} files.");
        job.Start();
    }

    /// <summary>
    /// The download's sign-in expired or was rejected: try a silent refresh first, then ask the user to
    /// sign in again (as the same account) on the download screen.
    /// </summary>
    private async Task<bool> ReauthenticateForDownloadAsync(CancellationToken ct)
    {
        try
        {
            if (await _auth!.TryRefreshSilentlyAsync(ct))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"Silent refresh failed: {ex.GetType().Name}: {ex.Message}");
        }

        return Download is { } download && await download.RequestSignInAsync(ct);
    }

    /// <summary>Whether closing the window should ask first (a download is running).</summary>
    public bool IsDownloadRunning => Download?.IsActive == true;

    /// <summary>Pauses a running download so its partial files can continue next time.</summary>
    public Task PauseDownloadForExitAsync() => Download?.PauseForExitAsync() ?? Task.CompletedTask;

    private void OnTreeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowseTreeViewModel.HasSelection))
        {
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task OpenTargetAsync(BlobTarget target, CancellationToken ct)
    {
        StatusText = target.Kind == BlobTargetKind.Account ? "Loading shared folders…" : "Opening the shared folder…";
        var count = await Tree.OpenAsync(target, ct);
        RefreshCommand.NotifyCanExecuteChanged();
        StatusText = count == 0
            ? "There are no shared folders you can open with this account."
            : "Tick the files and folders you want to download.";
    }

    private async Task SignInAsync(CancellationToken ct)
    {
        StatusText = _auth!.UsesBroker ? "Waiting for you to sign in…" : "Waiting for you to sign in in your browser…";
        User = await _auth.SignInInteractiveAsync(ct);
    }

    private async Task RunAsync(string busyText, Func<CancellationToken, Task> action)
    {
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        Error = null;
        StatusText = busyText;
        IsBusy = true;
        try
        {
            await action(_operation.Token);
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleError(Exception ex)
    {
        if (ex is SignInRequiredException)
        {
            User = null;
        }

        Error = ErrorTranslator.Translate(ex);
        StatusText = Error.Title;
        if (ex is not OperationCanceledException)
        {
            AppLog.Error($"{Error.Title} [{Error.Code}]", ex);
        }
    }
}
