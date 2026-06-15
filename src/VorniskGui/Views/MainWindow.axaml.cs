using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VorniskGui.ViewModels;

namespace VorniskGui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        BrowseSourceFileButton.Click   += BrowseSourceFile_Click;
        BrowseSourceFolderButton.Click += BrowseSourceFolder_Click;
        BrowseDestButton.Click         += BrowseDest_Click;

        // Drag-and-drop: drop a file/folder onto either box to fill it.
        SourceBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        SourceBox.AddHandler(DragDrop.DropEvent, OnDropSource);
        DestBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DestBox.AddHandler(DragDrop.DropEvent, OnDropDest);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ── pickers ──────────────────────────────────────────────────────────────────────

    private async void BrowseSourceFile_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Select source file", AllowMultiple = false });
        if (files.Count > 0)
            Vm.SetSource(LocalPath(files[0]));
    }

    private async void BrowseSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dirs = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select source folder", AllowMultiple = false });
        if (dirs.Count > 0)
            Vm.SetSource(LocalPath(dirs[0]));
    }

    private async void BrowseDest_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dirs = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select destination folder", AllowMultiple = false });
        if (dirs.Count > 0)
            Vm.SetDestination(LocalPath(dirs[0]));
    }

    // ── drag-and-drop ────────────────────────────────────────────────────────────────

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropSource(object? sender, DragEventArgs e)
    {
        var path = FirstDroppedPath(e);
        if (path is not null) Vm?.SetSource(path);
        e.Handled = true;
    }

    private void OnDropDest(object? sender, DragEventArgs e)
    {
        var path = FirstDroppedPath(e);
        if (path is not null) Vm?.SetDestination(path);
        e.Handled = true;
    }

    private static string? FirstDroppedPath(DragEventArgs e)
    {
        var first = e.Data.GetFiles()?.FirstOrDefault();
        return first is null ? null : (first.TryGetLocalPath() ?? first.Path.LocalPath);
    }

    private static string LocalPath(IStorageItem item) => item.TryGetLocalPath() ?? item.Path.LocalPath;
}
