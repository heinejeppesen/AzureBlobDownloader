using AzureBlobDownloader.Core.Auth;
using AzureBlobDownloader.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBlobDownloader.App.ViewModels;

/// <summary>
/// Asks for the organisation (domain or Tenant ID) and the Client ID when the app ships without them
/// (first start), or when the user chooses to change them. A domain is looked up and the Tenant ID is
/// saved in the per-user config file, so the tenant is pinned from then on.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly Func<Guid, Guid, CancellationToken, Task> _save;
    private readonly Action _cancel;
    private readonly Func<string, CancellationToken, Task<TenantLookup>> _lookupTenant;

    /// <param name="save">Saves the values and applies them; throws when that fails.</param>
    /// <param name="cancel">Leaves the screen without changes (only offered when not first run).</param>
    /// <param name="lookupTenant">Finds the Tenant ID for a domain or Tenant ID (replaceable in tests).</param>
    public SetupViewModel(
        string? tenantId,
        string? clientId,
        bool isFirstRun,
        string? savePath,
        Func<Guid, Guid, CancellationToken, Task> save,
        Action cancel,
        Func<string, CancellationToken, Task<TenantLookup>>? lookupTenant = null)
    {
        _save = save;
        _cancel = cancel;
        _lookupTenant = lookupTenant ?? ((tenant, ct) => TenantCheck.LookupAsync(tenant, ct));
        IsFirstRun = isFirstRun;
        SavePathText = savePath is null ? null : $"Saved for your user account in {savePath}";
        TenantIdText = IsPlaceholder(tenantId) ? string.Empty : tenantId!;
        ClientIdText = IsPlaceholder(clientId) ? string.Empty : clientId!;
    }

    public bool IsFirstRun { get; }

    public string Heading => IsFirstRun ? "Set up Azure Blob Downloader" : "Change settings";

    public string IntroText => IsFirstRun
        ? "Before you can open shared folders, enter the two values you received from the organisation that shares the files. You only need to do this once."
        : "Changing these values signs you out. Enter the values you received from the organisation that shares the files.";

    public string? SavePathText { get; }

    /// <summary>The organisation's domain (e.g. contoso.com) or its Tenant ID.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TenantIdError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string TenantIdText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClientIdError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string ClientIdText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CancelCommand))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string? SaveError { get; set; }

    public string? TenantIdError =>
        string.IsNullOrWhiteSpace(TenantIdText) || TenantCheck.TryParseTenant(TenantIdText, out _)
            ? null
            : "Enter the organisation's domain (e.g. contoso.com) or its Tenant ID (e.g. 12345678-abcd-1234-abcd-1234567890ab).";

    public string? ClientIdError =>
        string.IsNullOrWhiteSpace(ClientIdText) || TryParseId(ClientIdText, out _)
            ? null
            : "This is not a valid Client ID. It looks like 12345678-abcd-1234-abcd-1234567890ab.";

    public bool CanCancel => !IsFirstRun;

    /// <summary>Accepts the usual ways a GUID is copied: with or without braces, any case, surrounding spaces.</summary>
    public static bool TryParseId(string? text, out Guid id)
    {
        id = Guid.Empty;
        var trimmed = text?.Trim().Trim('{', '}', '"', '\'').Trim();
        return Guid.TryParseExact(trimmed, "D", out id) && id != Guid.Empty;
    }

    private bool CanSave() => !IsSaving && TenantCheck.TryParseTenant(TenantIdText, out _) && TryParseId(ClientIdText, out _);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!TenantCheck.TryParseTenant(TenantIdText, out var tenant) || !TryParseId(ClientIdText, out var clientId))
        {
            return;
        }

        SaveError = null;
        IsSaving = true;
        try
        {
            var isTenantId = Guid.TryParse(tenant, out var enteredId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var lookup = await _lookupTenant(tenant, timeout.Token);

            Guid tenantId;
            switch (lookup.Result)
            {
                case TenantCheckResult.Exists when lookup.TenantId is { } found:
                    tenantId = found;
                    break;
                case TenantCheckResult.NotFound:
                    SaveError = isTenantId
                        ? "No organisation (tenant) with this Tenant ID exists. Check the value you received."
                        : $"No organisation uses the domain \"{tenant}\" for signing in. Check the spelling, or enter the Tenant ID instead.";
                    return;
                case TenantCheckResult.Unknown when isTenantId:
                    tenantId = enteredId; // Cannot check now (offline); the sign-in will tell.
                    break;
                default:
                    SaveError = "The domain could not be looked up right now. Check your internet connection, or enter the Tenant ID instead.";
                    return;
            }

            if (!isTenantId)
            {
                AppLog.Info($"Domain {tenant} belongs to tenant {tenantId}.");
            }

            await _save(tenantId, clientId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SaveError = $"The settings could not be saved: {ex.Message}";
            AppLog.Error("Saving settings failed.", ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanCancelNow() => CanCancel && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanCancelNow))]
    private void Cancel() => _cancel();

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || (Guid.TryParse(value, out var g) && g == Guid.Empty);
}
