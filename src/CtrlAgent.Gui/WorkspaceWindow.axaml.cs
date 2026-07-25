using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CtrlAgent.Gui;

/// <summary>One row in the recent-workspaces list.</summary>
public sealed record WorkspaceEntry(string Name, string Path);

/// <summary>
/// Picks the directory the agent works in: recent workspaces, or Browse for a
/// new one. Returns the chosen absolute path from <see cref="ShowDialog{T}"/>,
/// or null if the user backed out.
/// </summary>
public sealed partial class WorkspaceWindow : Window
{
    private string? _result;

    public WorkspaceWindow()
    {
        InitializeComponent();
    }

    public WorkspaceWindow(string currentPath, IReadOnlyList<string> recent)
        : this()
    {
        CurrentPath.Text = currentPath;

        // The current directory is not offered as somewhere to switch to.
        var entries = recent
            .Where(path => !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new WorkspaceEntry(DisplayName(path), path))
            .ToList();

        RecentList.ItemsSource = entries;
        EmptyHint.IsVisible = entries.Count == 0;
        if (entries.Count > 0)
        {
            RecentList.SelectedIndex = 0;
        }
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a workspace",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            _result = path;
            Close(_result);
        }
    }

    private void OnOpenSelected(object? sender, RoutedEventArgs eventArgs)
    {
        if (RecentList.SelectedItem is WorkspaceEntry entry)
        {
            _result = entry.Path;
            Close(_result);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
