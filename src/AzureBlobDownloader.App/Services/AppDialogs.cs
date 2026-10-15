using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AzureBlobDownloader.Core.Diagnostics;

namespace AzureBlobDownloader.App.Services;

/// <summary>Dialogs and shell actions the view models need from the window.</summary>
public interface IAppDialogs
{
    /// <summary>Native folder picker. Returns the chosen folder's path, or null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? suggestedPath);

    /// <summary>Asks a yes/no question; returns true for <paramref name="confirm"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel);

    /// <summary>Opens a folder in the system file manager.</summary>
    void OpenFolder(string path);
}

public sealed class WindowDialogs(Window owner) : IAppDialogs
{
    public async Task<string?> PickFolderAsync(string title, string? suggestedPath)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(suggestedPath) && Directory.Exists(suggestedPath))
        {
            start = await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(suggestedPath)));
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var yes = new Button { Content = confirm, Classes = { "accent" }, IsDefault = true };
        var no = new Button { Content = cancel, IsCancel = true };
        yes.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { yes, no },
                },
            },
        };

        return dialog.ShowDialog(owner).ContinueWith(_ => result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OpenFolder(string path)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe")
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open")
                : new ProcessStartInfo("xdg-open");
            start.ArgumentList.Add(path);
            start.UseShellExecute = false;
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not open the folder: {ex.Message}");
        }
    }
}
