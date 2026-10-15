using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using AzureBlobDownloader.App.Services;
using AzureBlobDownloader.App.ViewModels;

namespace AzureBlobDownloader.App.Views;

public partial class MainWindow : Window
{
    private readonly WindowDialogs _dialogs;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _dialogs = new WindowDialogs(this);
        Opened += OnOpened;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Asks before closing while files are downloading; pauses so partial files can continue next time.</summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || ViewModel is not { IsDownloadRunning: true } vm)
        {
            return;
        }

        e.Cancel = true;
        var close = await _dialogs.ConfirmAsync(
            "Download in progress",
            "Files are still downloading. Close anyway?\n\nTo continue later, open the same link and download to the same folder: "
            + "finished files are skipped and partly downloaded files continue where they stopped.",
            "Close",
            "Keep downloading");
        if (close)
        {
            await vm.PauseDownloadForExitAsync();
            _closeConfirmed = true;
            Close();
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.ParentWindowHandle = () => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        vm.Dialogs = _dialogs;

        try
        {
            if (Clipboard is { } clipboard)
            {
                vm.OfferClipboardText(await clipboard.TryGetTextAsync());
            }
        }
        catch
        {
            // The clipboard is a convenience only.
        }

        await vm.InitializeAsync();
    }

    /// <summary>Double-clicking a folder row opens or closes it (but not when double-clicking its check box).</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
        {
            return;
        }

        if (ViewModel?.Tree.SelectedRow is { CanExpand: true } row)
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    /// <summary>Keyboard: Space ticks, Right/Left open/close (Left on a closed row goes to its folder), Enter toggles.</summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.Tree is not { } tree)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                tree.ToggleSelectedCheck();
                break;
            case Key.Right:
                tree.ExpandSelected(true);
                break;
            case Key.Left:
                tree.ExpandSelected(false);
                break;
            case Key.Enter when tree.SelectedRow is { CanExpand: true } row:
                row.IsExpanded = !row.IsExpanded;
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
