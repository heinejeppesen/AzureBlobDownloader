using System.ComponentModel;
using Avalonia;
using AzureBlobDownloader.Core.Browsing;
using AzureBlobDownloader.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBlobDownloader.App.ViewModels;

/// <summary>Receives what the user does on a row.</summary>
public interface ITreeRowHost
{
    void SetChecked(TreeRow row, bool value);

    void OnExpandedChanged(TreeRow row, bool expanded);

    void RequestCount(TreeRow row);
}

/// <summary>
/// One visible row of the browse tree. The tree is shown as a flat, virtualized list (indentation per
/// level), so folders with very many files stay fast.
/// </summary>
public sealed partial class TreeRow : ObservableObject
{
    private const double IndentPerLevel = 20;

    private readonly ITreeRowHost _host;

    public TreeRow(BrowseNode node, ITreeRowHost host)
    {
        Node = node;
        _host = host;
        node.PropertyChanged += OnNodeChanged;
    }

    public BrowseNode Node { get; }

    /// <summary>The pending or finished listing of this row's children.</summary>
    internal Task<bool>? LoadTask { get; set; }

    public Thickness Indent => new(Node.Depth * IndentPerLevel, 0, 0, 0);

    public string Name => Node.Name;

    public bool IsFile => Node.IsFile;

    public bool CanExpand => !Node.IsFile;

    /// <summary>Two-way bound to the check box; the host applies the change to the whole tree.</summary>
    public bool? IsChecked
    {
        get => Node.IsChecked;
        set
        {
            // A click on a partly checked box selects everything below it.
            _host.SetChecked(this, value != false);
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(ShowCountButton))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(ShowCountButton))]
    public partial bool IsCounting { get; set; }

    /// <summary>"Loading… 5,000" / "12,000 files…" while busy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial string? ProgressText { get; set; }

    public string SizeText
    {
        get
        {
            if (IsLoading || IsCounting)
            {
                return ProgressText ?? (IsLoading ? "Loading…" : "Counting…");
            }

            if (Node.IsFile)
            {
                return Node.Size is { } size ? ByteSize.Format(size) : string.Empty;
            }

            return Node.Totals is { } totals ? ByteSize.Format(totals.Bytes) : string.Empty;
        }
    }

    public bool ShowCountButton => !Node.IsFile && Node.Totals is null && !IsCounting && !IsLoading;

    public string? DetailText => Node.Totals is { } t
        ? $"{t.FileCount:N0} files, {ByteSize.Format(t.Bytes)}, {t.FilesWithMd5:N0} with MD5"
        : null;

    public string ModifiedText => Node.LastModified?.ToLocalTime().ToString("g") ?? string.Empty;

    /// <summary>Whether downloads can be checked against an MD5 hash (otherwise size only).</summary>
    public string Md5Text
    {
        get
        {
            if (Node.IsFile)
            {
                return Node.HasContentMd5 == true ? "yes" : "no";
            }

            return Node.Totals switch
            {
                null => string.Empty,
                { FileCount: 0 } => string.Empty,
                var t when t.FilesWithMd5 == t.FileCount => "all",
                { FilesWithMd5: 0 } => "none",
                var t => $"{t.FilesWithMd5 * 100 / t.FileCount}%",
            };
        }
    }

    [RelayCommand]
    private void Count() => _host.RequestCount(this);

    partial void OnIsExpandedChanged(bool value) => _host.OnExpandedChanged(this, value);

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowseNode.IsChecked):
                OnPropertyChanged(nameof(IsChecked));
                break;
            case nameof(BrowseNode.Totals):
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(ShowCountButton));
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(Md5Text));
                break;
        }
    }
}
