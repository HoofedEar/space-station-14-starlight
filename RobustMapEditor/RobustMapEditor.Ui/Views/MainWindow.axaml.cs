#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RobustMapEditor.Ui.ViewModels;

namespace RobustMapEditor.Ui.Views;

public partial class MainWindow : Window
{
    private bool _bypassCloseGuard;

    public MainWindow()
    {
        InitializeComponent();

        // Global shortcuts.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control) });                       // Open
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control) });                       // Save
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift) });  // Save As
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control) });                       // Undo
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Y, KeyModifiers.Control) });                       // Redo
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift) });  // Redo alt

        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.PickMapFileAsync    = PickMapFileAsync;
        vm.PickSaveAsFileAsync = PickSaveAsFileAsync;
        vm.ConfirmDiscardAsync = ConfirmDiscardAsync;

        // Canvas needs a VM reference so the paint tool can read SelectedTile
        // and kick off ExecuteCommandAsync on stroke commit.
        MapCanvasControl.ViewModel = vm;

        vm.PropertyChanged += OnViewModelPropertyChanged;

        // Post-render refresh hook. ExecuteCommandAsync / Undo / Redo all swap
        // MapDocument.Grids in place without re-assigning Document, so we need
        // this explicit channel to tell the canvas to rebuild its bitmaps.
        vm.OnBitmapsInvalidated = () => MapCanvasControl.RefreshBitmaps();

        if (KeyBindings.Count >= 6)
        {
            KeyBindings[0].Command = vm.OpenMapCommand;
            KeyBindings[1].Command = vm.SaveCommand;
            KeyBindings[2].Command = vm.SaveAsCommand;
            KeyBindings[3].Command = vm.UndoCommand;
            KeyBindings[4].Command = vm.RedoCommand;
            KeyBindings[5].Command = vm.RedoCommand; // Ctrl+Shift+Z alt
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.Document):
                MapCanvasControl.Document = vm.Document;
                break;
            case nameof(MainWindowViewModel.SelectedTile):
            case nameof(MainWindowViewModel.SelectedEntity):
                // Redraw so the hover ghost reflects the new tool mode — in entity mode
                // the ghost suppresses itself; in tile mode it follows the cursor. The
                // tile under the cursor didn't move, but the thing we'd do there changed.
                MapCanvasControl.InvalidateVisual();
                break;
            case nameof(MainWindowViewModel.SelectedMapEntity):
                // Auto-focus the Inspector tab (index 2) when a fresh selection arrives.
                // We don't auto-revert when it clears — the user should stay on Inspector
                // if they were browsing it, rather than being yanked back to Tiles.
                if (vm.SelectedMapEntity != null)
                    PaletteTabs.SelectedIndex = 2;
                // Redraw: the canvas draws a highlight rect around the selected entity's
                // tile, so a change in selection (including to null) needs a repaint.
                MapCanvasControl.InvalidateVisual();
                break;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_bypassCloseGuard)
            return;
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (!vm.IsDirty)
            return;

        e.Cancel = true;
        var ok = await vm.PromptDiscardIfDirtyAsync("Close the editor");
        if (ok)
        {
            _bypassCloseGuard = true;
            Close();
        }
    }

    /// <summary>Palette button click → update SelectedTile. Radio-style: only one at a time.</summary>
    private void OnTileEntryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not TilePaletteItem item) return;
        if (DataContext is not MainWindowViewModel vm) return;

        // Toggle: clicking the selected tile again clears the selection (exits paint mode).
        vm.SelectedTile = ReferenceEquals(vm.SelectedTile, item) ? null : item;
    }

    private async Task<string?> PickMapFileAsync()
    {
        var top = GetTopLevel(this);
        if (top == null) return null;

        IStorageFolder? startFolder = null;
        try
        {
            var appDir = AppContext.BaseDirectory;
            var mapsDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "Resources", "Maps"));
            if (Directory.Exists(mapsDir))
                startFolder = await top.StorageProvider.TryGetFolderFromPathAsync(mapsDir);
        }
        catch { /* ignore */ }

        var result = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open map",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Map files (*.yml, *.yaml)")
                {
                    Patterns = new[] { "*.yml", "*.yaml" }
                },
                FilePickerFileTypes.All
            }
        });

        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveAsFileAsync(string? currentPath)
    {
        var top = GetTopLevel(this);
        if (top == null) return null;

        IStorageFolder? startFolder = null;
        string? suggestedName = null;
        try
        {
            if (!string.IsNullOrEmpty(currentPath))
            {
                var dir = Path.GetDirectoryName(currentPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    startFolder = await top.StorageProvider.TryGetFolderFromPathAsync(dir);
                suggestedName = Path.GetFileName(currentPath);
            }
        }
        catch { /* fallback */ }

        var result = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save map as",
            SuggestedStartLocation = startFolder,
            SuggestedFileName = suggestedName,
            DefaultExtension = "yml",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Map files (*.yml, *.yaml)")
                {
                    Patterns = new[] { "*.yml", "*.yaml" }
                }
            }
        });

        return result?.TryGetLocalPath();
    }

    private async Task<MainWindowViewModel.ConfirmResult> ConfirmDiscardAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<MainWindowViewModel.ConfirmResult>();

        var saveBtn    = new Button { Content = "Save",    IsDefault = true, MinWidth = 80 };
        var discardBtn = new Button { Content = "Discard",                   MinWidth = 80 };
        var cancelBtn  = new Button { Content = "Cancel",  IsCancel  = true, MinWidth = 80 };

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { saveBtn, discardBtn, cancelBtn }
                    }
                }
            }
        };

        saveBtn.Click    += (_, _) => { tcs.TrySetResult(MainWindowViewModel.ConfirmResult.Save);    dialog.Close(); };
        discardBtn.Click += (_, _) => { tcs.TrySetResult(MainWindowViewModel.ConfirmResult.Discard); dialog.Close(); };
        cancelBtn.Click  += (_, _) => { tcs.TrySetResult(MainWindowViewModel.ConfirmResult.Cancel);  dialog.Close(); };
        dialog.Closed    += (_, _) => tcs.TrySetResult(MainWindowViewModel.ConfirmResult.Cancel);

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnResetViewClicked(object? sender, RoutedEventArgs e) => MapCanvasControl.ResetView();
}
