#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RobustMapEditor.Core.Commands;
using RobustMapEditor.Core.Documents;
using RobustMapEditor.Core.Entities;
using RobustMapEditor.Core.Io;
using RobustMapEditor.Core.Session;
using RobustMapEditor.Core.Tiles;
using Robust.Shared.GameObjects;
using Vector2i = Robust.Shared.Maths.Vector2i;

namespace RobustMapEditor.Ui.ViewModels;

/// <summary>
/// Top-level view-model. Owns the persistent <see cref="EditorSession"/> (for
/// the prototype catalog), the currently-open <see cref="MapDocument"/>, and
/// the tile palette + selection for Phase 2B paint tooling.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitializing), nameof(IsReady), nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(OpenMapCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private LoadState _state = LoadState.Idle;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenMapCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedEntityCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasDocument))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedEntityCommand))]
    private MapDocument? _document;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;

    /// <summary>Items displayed in the right-hand tile palette. Replaced wholesale
    /// whenever the document changes. The old list's items are disposed before
    /// they're released so Avalonia bitmaps don't leak.</summary>
    public ObservableCollection<TilePaletteItem> TilePalette { get; } = new();

    /// <summary>Currently selected palette entry. Paint strokes use this tile's TypeId.
    /// When null, left-click-drag stays on its default pan-gesture path.</summary>
    [ObservableProperty]
    private TilePaletteItem? _selectedTile;

    /// <summary>Items displayed in the entity palette. This is the <i>filtered</i> view —
    /// the full set lives in <see cref="_allEntities"/> and is reprojected whenever
    /// <see cref="EntitySearchQuery"/> changes. Bound to a virtualized ListBox so only
    /// on-screen rows are realized (the full catalog is ~thousands of prototypes).</summary>
    public ObservableCollection<EntityPaletteItem> EntityPalette { get; } = new();

    /// <summary>Backing store of every palette item built for the current document.
    /// <see cref="EntityPalette"/> is a filtered projection of this list. Kept separate so
    /// we can re-filter without rebuilding thumbnails.</summary>
    private readonly List<EntityPaletteItem> _allEntities = new();

    /// <summary>Currently selected entity prototype. Left-click on the canvas spawns
    /// this prototype. Mutually exclusive with <see cref="SelectedTile"/> — selecting
    /// one clears the other so the tool mode on the canvas is unambiguous.</summary>
    [ObservableProperty]
    private EntityPaletteItem? _selectedEntity;

    /// <summary>Filter text for the entity palette. Case-insensitive substring match
    /// against name and prototype ID. Empty string = show everything.</summary>
    [ObservableProperty]
    private string _entitySearchQuery = string.Empty;

    /// <summary>Entity currently chosen via right-click → context menu. Feeds the
    /// Inspector tab. Null when nothing is selected for inspection. Survives tool-mode
    /// toggles (tile ↔ entity) so the user can inspect, then switch tools, and still
    /// see their selection.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedEntityCommand))]
    private InspectedEntity? _selectedMapEntity;

    public bool IsInitializing => State is LoadState.Initializing;
    public bool IsReady        => State is LoadState.Ready;
    public bool HasError       => State is LoadState.Error;

    public bool HasDocument => Document != null;

    public string WindowTitle
    {
        get
        {
            if (Document == null)
                return "RobustMapEditor";
            var name = Path.GetFileName(Document.FilePath);
            return IsDirty ? $"RobustMapEditor — {name}*" : $"RobustMapEditor — {name}";
        }
    }

    private readonly EditorSession _session = new();

    /// <summary>Serializes command execution so two rapid clicks (or a click landing during
    /// an undo/redo) can't race through the engine pair + renderer concurrently. Without
    /// this, two <see cref="ExecuteCommandAsync"/> calls each construct their own painter
    /// pair, snapshot <c>_grids</c> on independent awaits, and dispose each other's
    /// <see cref="Documents.RenderedGrid"/> out from under the canvas.</summary>
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>True iff a paint/undo/redo is currently executing. Tools (e.g. MapCanvas)
    /// read this to drop new strokes rather than queuing them behind a long-running one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedEntityCommand))]
    private bool _isExecutingCommand;

    /// <summary>UI-supplied native file picker for Open.</summary>
    public Func<Task<string?>>? PickMapFileAsync { get; set; }

    /// <summary>UI-supplied native file picker for Save-As (write target).</summary>
    public Func<string?, Task<string?>>? PickSaveAsFileAsync { get; set; }

    /// <summary>UI-supplied confirm prompt.</summary>
    public Func<string, string, Task<ConfirmResult>>? ConfirmDiscardAsync { get; set; }

    /// <summary>Raised after the document's underlying grid images change without a
    /// document instance swap — i.e. after ExecuteCommandAsync / UndoAsync / RedoAsync
    /// rerender in place. The view uses this to tell <c>MapCanvas</c> to rebuild its
    /// Avalonia bitmaps.</summary>
    public Action? OnBitmapsInvalidated { get; set; }

    public void BeginInitialization()
    {
        if (State != LoadState.Idle)
            return;

        State = LoadState.Initializing;
        StatusMessage = "Starting PoolManager and spawning server/client pair...";

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _session.InitializeAsync();
            var protoCount = _session.GetGameMapPrototypeIds().Count;
            StatusMessage = $"Engine ready. {protoCount} map prototypes loaded. Use File > Open to load a map.";
            State = LoadState.Ready;
        }
        catch (Exception ex)
        {
            ErrorMessage  = ex.ToString();
            StatusMessage = "Engine bootstrap failed.";
            State         = LoadState.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenMap))]
    private async Task OpenMap()
    {
        if (PickMapFileAsync == null)
            return;

        if (!await PromptDiscardIfDirtyAsync("Open another map"))
            return;

        var path = await PickMapFileAsync();
        if (string.IsNullOrEmpty(path))
            return;

        await OpenAtPathAsync(path);
    }

    private async Task OpenAtPathAsync(string path)
    {
        IsBusy = true;
        StatusMessage = $"Loading {Path.GetFileName(path)}...";

        try
        {
            // Clear any stale inspector selection — it refers to an EntityUid from the
            // previous document's pair and has no meaning after the swap.
            SelectedMapEntity = null;

            var previous = Document;
            var doc = await MapDocument.OpenAsync(path);
            Document = doc;
            RefreshDocumentState();

            // Tear down the old doc after swap so the canvas has already released its references.
            if (previous != null)
                await previous.DisposeAsync();

            // Build the palettes for this document's session. Non-blocking for the UI —
            // we still mark the doc ready; placement/painting just disables until they load.
            DisposePalettes();
            StatusMessage = $"Loaded {Path.GetFileName(path)} — {doc.Grids.Count} grid(s). Loading palettes...";

            var tileEntries = await doc.BuildTileCatalogAsync();
            foreach (var entry in tileEntries)
                TilePalette.Add(new TilePaletteItem(entry));

            var entityEntries = await doc.BuildEntityCatalogAsync();
            foreach (var entry in entityEntries)
                _allEntities.Add(new EntityPaletteItem(entry));
            RebuildEntityPaletteView();

            StatusMessage = $"Loaded {Path.GetFileName(path)} — {doc.Grids.Count} grid(s), " +
                            $"{TilePalette.Count} tile defs, {_allEntities.Count} entity protos.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load map: {ex.Message}";
            ErrorMessage = ex.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Execute an edit command and refresh dirty + can-undo/redo projections.
    /// Called by tools (paint, future erase/flood-fill). Re-renders inside MapDocument.
    /// Serialized via <see cref="_commandGate"/>: back-to-back clicks or an Undo fired
    /// while a paint is mid-render wait for the previous command to finish — the engine
    /// pair and renderer are not re-entrant.</summary>
    public async Task ExecuteCommandAsync(IEditCommand command)
    {
        if (Document == null) return;

        await _commandGate.WaitAsync();
        IsExecutingCommand = true;
        try
        {
            // Document could have been closed/swapped while we were waiting at the gate.
            if (Document == null) return;

            await Document.ExecuteAsync(command);
            RefreshDocumentState();
            OnBitmapsInvalidated?.Invoke();
            StatusMessage = command.Description;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Command failed: {ex.Message}";
            ErrorMessage = ex.ToString();
        }
        finally
        {
            IsExecutingCommand = false;
            _commandGate.Release();
        }
    }

    /// <summary>Hit-test wrapper for the canvas: returns entities directly parented to
    /// <paramref name="gridUid"/> that sit on the given tile. Gated by the command
    /// semaphore so a lookup fired during an in-flight paint stroke waits for it to
    /// finish rather than seeing a half-applied world.</summary>
    public async Task<IReadOnlyList<InspectedEntity>> GetEntitiesAtTileAsync(
        EntityUid gridUid,
        Vector2i tile)
    {
        if (Document == null)
            return Array.Empty<InspectedEntity>();

        await _commandGate.WaitAsync();
        try
        {
            if (Document == null)
                return Array.Empty<InspectedEntity>();
            return await Document.GetEntitiesAtTileAsync(gridUid, tile);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>Store an inspected entity on <see cref="SelectedMapEntity"/>, pulling a
    /// fresh snapshot from the engine so the inspector shows current component state
    /// even if the caller's copy is stale. Pass null to clear selection.</summary>
    public async Task SelectEntityAsync(EntityUid? uid)
    {
        if (uid is not { } u || Document == null)
        {
            SelectedMapEntity = null;
            return;
        }

        await _commandGate.WaitAsync();
        try
        {
            if (Document == null)
            {
                SelectedMapEntity = null;
                return;
            }
            SelectedMapEntity = await Document.InspectEntityAsync(u);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>Delete the entity currently in the Inspector. Clears the selection
    /// before dispatching because the target uid becomes invalid on apply; an undo
    /// re-spawns with a new uid, so keeping the old selection would point at a
    /// ghost. Gated through <see cref="ExecuteCommandAsync"/> like any other edit.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedEntity))]
    private async Task DeleteSelectedEntity()
    {
        if (SelectedMapEntity is not { } sel) return;

        var cmd = new DeleteEntityCommand(sel.Uid);
        // Clear selection *before* dispatch — the underlying uid is about to be
        // destroyed, and the highlight shouldn't linger on stale coordinates.
        SelectedMapEntity = null;
        await ExecuteCommandAsync(cmd);
    }

    private bool CanDeleteSelectedEntity()
        => IsReady && !IsBusy && !IsExecutingCommand && Document != null && SelectedMapEntity != null;

    [RelayCommand(CanExecute = nameof(CanUndoCmd))]
    private async Task Undo()
    {
        if (Document == null) return;

        await _commandGate.WaitAsync();
        IsExecutingCommand = true;
        try
        {
            if (Document == null) return;
            var desc = Document.History.UndoDescription;
            var ok = await Document.UndoAsync();
            if (ok)
            {
                RefreshDocumentState();
                OnBitmapsInvalidated?.Invoke();
                StatusMessage = desc != null ? $"Undo: {desc}" : "Undo.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Undo failed: {ex.Message}";
            ErrorMessage = ex.ToString();
        }
        finally
        {
            IsExecutingCommand = false;
            _commandGate.Release();
        }
    }

    private bool CanUndoCmd() => IsReady && !IsBusy && Document != null && CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedoCmd))]
    private async Task Redo()
    {
        if (Document == null) return;

        await _commandGate.WaitAsync();
        IsExecutingCommand = true;
        try
        {
            if (Document == null) return;
            var desc = Document.History.RedoDescription;
            var ok = await Document.RedoAsync();
            if (ok)
            {
                RefreshDocumentState();
                OnBitmapsInvalidated?.Invoke();
                StatusMessage = desc != null ? $"Redo: {desc}" : "Redo.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Redo failed: {ex.Message}";
            ErrorMessage = ex.ToString();
        }
        finally
        {
            IsExecutingCommand = false;
            _commandGate.Release();
        }
    }

    private bool CanRedoCmd() => IsReady && !IsBusy && Document != null && CanRedo;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (Document == null) return;

        IsBusy = true;
        StatusMessage = $"Saving {Path.GetFileName(Document.FilePath)}...";
        try
        {
            await MapSaveService.SaveAsync(Document);
            Document.MarkClean();
            RefreshDocumentState();
            StatusMessage = $"Saved {Path.GetFileName(Document.FilePath)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            ErrorMessage = ex.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => IsReady && !IsBusy && Document != null && IsDirty;

    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAs()
    {
        if (Document == null || PickSaveAsFileAsync == null) return;

        var target = await PickSaveAsFileAsync(Document.FilePath);
        if (string.IsNullOrEmpty(target))
            return;

        IsBusy = true;
        StatusMessage = $"Saving to {Path.GetFileName(target)}...";
        try
        {
            await MapSaveService.SaveAsAsync(Document, target);
            Document.MarkClean();
            RefreshDocumentState();
            StatusMessage = $"Saved to {Path.GetFileName(target)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save-As failed: {ex.Message}";
            ErrorMessage = ex.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveAs() => IsReady && !IsBusy && Document != null;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task Revert()
    {
        if (Document == null) return;

        if (ConfirmDiscardAsync != null)
        {
            var r = await ConfirmDiscardAsync(
                "Revert to disk",
                "Discard unsaved changes and reload the file from disk?");
            if (r != ConfirmResult.Discard)
                return;
        }

        var path = Document.FilePath;
        await OpenAtPathAsync(path);
    }

    private bool CanRevert() => IsReady && !IsBusy && Document != null && IsDirty;

    /// <summary>Pull IsDirty + CanUndo/CanRedo from the document into the VM's
    /// observable projections. MapDocument doesn't raise property-changed events,
    /// so callers must invoke this after every mutation they drive.</summary>
    private void RefreshDocumentState()
    {
        if (Document == null)
        {
            IsDirty = false;
            CanUndo = false;
            CanRedo = false;
            return;
        }

        IsDirty = Document.IsDirty;
        CanUndo = Document.History.CanUndo;
        CanRedo = Document.History.CanRedo;
    }

    private void DisposePalettes()
    {
        foreach (var item in TilePalette)
            item.Dispose();
        TilePalette.Clear();
        SelectedTile = null;

        // Entity items own Avalonia bitmaps (thumbnails) + the Core catalog entry's
        // ImageSharp image, so both the filtered view and the master list need to be
        // cleared and their items disposed to release GPU-side textures.
        SelectedEntity = null;
        EntityPalette.Clear();
        foreach (var item in _allEntities)
            item.Dispose();
        _allEntities.Clear();
    }

    /// <summary>Project <see cref="_allEntities"/> into <see cref="EntityPalette"/>
    /// using the current <see cref="EntitySearchQuery"/>. Rebuilds the ObservableCollection
    /// in place so the virtualized ListBox stays bound.</summary>
    /// <remarks>
    /// Cheap even for thousands of items: this only swaps object references in the
    /// collection, no thumbnail decoding. The ListBox's VirtualizingStackPanel then
    /// only realizes containers for currently on-screen rows.
    /// </remarks>
    private void RebuildEntityPaletteView()
    {
        EntityPalette.Clear();
        var q = EntitySearchQuery?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            foreach (var item in _allEntities)
                EntityPalette.Add(item);
            return;
        }

        foreach (var item in _allEntities)
        {
            if (item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                item.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                EntityPalette.Add(item);
            }
        }
    }

    partial void OnEntitySearchQueryChanged(string value)
    {
        RebuildEntityPaletteView();
        // If the selected entity got filtered out, clear the selection so the canvas's
        // tool mode doesn't stay locked onto a prototype the user can no longer see.
        if (SelectedEntity != null && !EntityPalette.Contains(SelectedEntity))
            SelectedEntity = null;
    }

    /// <summary>True → caller may proceed. False → user cancelled.</summary>
    public async Task<bool> PromptDiscardIfDirtyAsync(string actionLabel)
    {
        if (Document == null || !IsDirty)
            return true;

        if (ConfirmDiscardAsync == null)
            return true;

        var name = Path.GetFileName(Document.FilePath);
        var result = await ConfirmDiscardAsync(
            actionLabel,
            $"\"{name}\" has unsaved changes. Save before continuing?");

        switch (result)
        {
            case ConfirmResult.Save:
                await Save();
                return !IsDirty;
            case ConfirmResult.Discard:
                return true;
            case ConfirmResult.Cancel:
            default:
                return false;
        }
    }

    partial void OnSelectedTileChanged(TilePaletteItem? oldValue, TilePaletteItem? newValue)
    {
        // Radio-style selection indicator. The ItemsControl template draws a highlight
        // bound to IsSelected; keeping it in sync here lets the VM survive list re-bind.
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;

        // Tile and entity tools are mutually exclusive: picking a tile clears the
        // entity selection so the canvas doesn't get confused about which click
        // gesture to honor.
        if (newValue != null && SelectedEntity != null)
            SelectedEntity = null;
    }

    partial void OnSelectedEntityChanged(EntityPaletteItem? oldValue, EntityPaletteItem? newValue)
    {
        // The entity palette uses a real ListBox, so the selection visual comes from
        // ListBoxItem:selected styling — no IsSelected flag to keep in sync here.

        // Tile and entity tools are mutually exclusive: picking an entity clears the
        // tile selection so the canvas's click gesture has a single unambiguous target.
        if (newValue != null && SelectedTile != null)
            SelectedTile = null;
    }

    private bool CanOpenMap() => IsReady && !IsBusy;

    public enum LoadState
    {
        Idle,
        Initializing,
        Ready,
        Error,
    }

    public enum ConfirmResult
    {
        Save,
        Discard,
        Cancel,
    }
}
