#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RobustMapEditor.Core.Commands;
using RobustMapEditor.Core.Documents;
using RobustMapEditor.Core.Entities;
using RobustMapEditor.Ui.Rendering;
using RobustMapEditor.Ui.ViewModels;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Vector2i = Robust.Shared.Maths.Vector2i;

namespace RobustMapEditor.Ui.Views;

/// <summary>
/// Displays a loaded <see cref="MapDocument"/> with pan (middle-mouse drag or
/// Ctrl+drag) and zoom (wheel), plus Phase 2B tile painting: left-click paints
/// the palette's selected tile, left-drag paints a stroke batched into one
/// undoable command, right-click erases.
/// </summary>
public sealed class MapCanvas : Control
{
    /// <summary>World tile-units to pixels. Mirrors <c>EyeManager.PixelsPerMeter</c>.</summary>
    public const double PixelsPerTile = 32.0;

    private const double MinZoom = 0.05;
    private const double MaxZoom = 16.0;
    private const double ZoomStep = 1.15;

    // Per-grid cached bitmap. Built once when a document is set; disposed when replaced.
    private readonly List<(RenderedGrid Grid, WriteableBitmap Bitmap)> _renderedGrids = new();

    private MapDocument? _document;
    private MainWindowViewModel? _viewModel;

    // Pan state.
    private Vector _panOffset;
    private double _zoom = 1.0;
    private bool _panning;
    private Point _panStartPointer;
    private Vector _panStartOffset;

    // Paint-stroke state. Keyed by grid UID because a stroke that starts on one grid is
    // anchored there — dragging onto a neighbor doesn't spill the stroke over.
    private bool _painting;
    private bool _eraseStroke;
    private EntityUid _strokeGridUid;
    private readonly HashSet<Vector2i> _strokeCoords = new();
    private readonly List<(Vector2i Coords, Tile NewTile)> _strokeEdits = new();

    // Current hover for the ghost preview.
    private bool _hasHover;
    private RenderedGrid? _hoverGrid;
    private Vector2i _hoverTile;

    // Right-click-for-context-menu state. Recorded on press; consumed on release to open
    // the menu iff the pointer didn't drift (so a right-drag gesture can coexist in
    // future). The click is only "armed" when no tile tool is active — tile-paint mode
    // keeps its existing right-click-to-erase behavior.
    private bool _rightClickArmed;
    private Point _rightClickStart;
    private const double RightClickDragThreshold = 4.0;

    private static readonly IBrush BackgroundBrush =
        new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));

    private static readonly IPen HoverOutlinePen =
        new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x40, 0xC0, 0xFF)), 1.5);

    private static readonly IBrush HoverFillBrush =
        new SolidColorBrush(Color.FromArgb(0x40, 0x40, 0xC0, 0xFF));

    // Selection highlight — a warmer, more saturated outline than hover so the two
    // visual channels (what's under cursor vs. what's inspector-selected) stay
    // distinguishable. Thicker stroke too, since selection is a held state and the
    // user shouldn't have to squint.
    private static readonly IPen SelectionOutlinePen =
        new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC0, 0x40)), 2.0);

    public MapCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public MapDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value))
                return;

            DisposeBitmaps();
            _document = value;
            RebuildBitmaps();
            if (_document != null)
                ResetView();

            InvalidateVisual();
        }
    }

    /// <summary>The VM drives paint commands — the canvas reads SelectedTile from it and
    /// asks it to ExecuteCommandAsync when a stroke commits.</summary>
    public MainWindowViewModel? ViewModel
    {
        get => _viewModel;
        set => _viewModel = value;
    }

    /// <summary>Refresh bitmaps from the current document — call after a re-render
    /// (e.g. post-command) to pick up new <see cref="RenderedGrid"/> images. Diffs against
    /// the cached entries: bitmaps whose <see cref="RenderedGrid"/> reference is still
    /// present in the document are kept verbatim (the expensive 32bpp copy is skipped);
    /// only the replaced grids pay the upload cost. Orphaned bitmaps are disposed.</summary>
    public void RefreshBitmaps()
    {
        if (_document == null)
        {
            DisposeBitmaps();
            InvalidateVisual();
            return;
        }

        // Index existing entries by RenderedGrid reference so we can cheaply ask
        // "do we already have a bitmap for this RenderedGrid?".
        var existing = new Dictionary<RenderedGrid, WriteableBitmap>(_renderedGrids.Count);
        foreach (var (grid, bitmap) in _renderedGrids)
            existing[grid] = bitmap;

        var next = new List<(RenderedGrid Grid, WriteableBitmap Bitmap)>(_document.Grids.Count);
        var carried = new HashSet<RenderedGrid>(ReferenceEqualityComparer.Instance);
        foreach (var grid in _document.Grids)
        {
            if (existing.TryGetValue(grid, out var bitmap))
            {
                next.Add((grid, bitmap));
                carried.Add(grid);
            }
            else
            {
                next.Add((grid, ImageSharpBridge.ToAvaloniaBitmap(grid.Image)));
            }
        }

        // Dispose bitmaps for any RenderedGrid that is no longer in the document.
        foreach (var (grid, bitmap) in _renderedGrids)
        {
            if (!carried.Contains(grid))
                bitmap.Dispose();
        }

        _renderedGrids.Clear();
        _renderedGrids.AddRange(next);

        // Hover state holds a RenderedGrid reference; if that grid just got swapped out by
        // a partial re-render, drop the hover so the ghost doesn't draw against a disposed
        // image. The next PointerMoved will re-hit-test.
        if (_hasHover && _hoverGrid != null && !carried.Contains(_hoverGrid))
        {
            _hasHover = false;
            _hoverGrid = null;
        }

        InvalidateVisual();
    }

    private void RebuildBitmaps()
    {
        if (_document == null) return;
        foreach (var grid in _document.Grids)
            _renderedGrids.Add((grid, ImageSharpBridge.ToAvaloniaBitmap(grid.Image)));
    }

    public void ResetView()
    {
        if (_document == null || _renderedGrids.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _panOffset = default;
            _zoom = 1.0;
            return;
        }

        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        foreach (var (grid, _) in _renderedGrids)
        {
            var gx = grid.WorldOffset.X * PixelsPerTile;
            var gy = grid.WorldOffset.Y * PixelsPerTile;
            minX = Math.Min(minX, gx);
            minY = Math.Min(minY, gy);
            maxX = Math.Max(maxX, gx + grid.PixelWidth);
            maxY = Math.Max(maxY, gy + grid.PixelHeight);
        }

        var docWidth  = Math.Max(1, maxX - minX);
        var docHeight = Math.Max(1, maxY - minY);

        const double margin = 0.92;
        _zoom = Math.Clamp(
            Math.Min(Bounds.Width / docWidth, Bounds.Height / docHeight) * margin,
            MinZoom, MaxZoom);

        var docCenter = new Vector((minX + maxX) / 2.0, (minY + maxY) / 2.0);
        var viewCenter = new Vector(Bounds.Width / 2.0, Bounds.Height / 2.0);
        _panOffset = viewCenter - docCenter * _zoom;
    }

    /// <summary>Convert a screen point to world pixel (pre-zoom, pre-pan).</summary>
    private Vector ScreenToWorldPixel(Point screen) =>
        new((screen.X - _panOffset.X) / _zoom, (screen.Y - _panOffset.Y) / _zoom);

    /// <summary>Find the topmost rendered grid containing a given world-pixel point, plus
    /// the grid-local tile coord at that point. Iterates in reverse so later-drawn grids
    /// win ties.</summary>
    private (RenderedGrid Grid, Vector2i Tile)? HitTestGrid(Vector worldPixel)
    {
        var wp = new System.Numerics.Vector2((float)worldPixel.X, (float)worldPixel.Y);
        for (var i = _renderedGrids.Count - 1; i >= 0; i--)
        {
            var grid = _renderedGrids[i].Grid;
            var tile = grid.TryWorldPixelToTile(wp);
            if (tile.HasValue && grid.GridUid is { } uid)
                return (grid, tile.Value);
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;

        // Pan gesture always wins: middle-drag, or Ctrl+left-drag.
        if (props.IsMiddleButtonPressed ||
            (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            _panning = true;
            _panStartPointer = e.GetPosition(this);
            _panStartOffset = _panOffset;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Tool gestures require a document.
        if (_document == null || _viewModel == null)
            return;

        // Drop presses while a previous command is still mid-render. The VM's semaphore
        // would otherwise queue a fresh action behind the first, and the user's intent is
        // "I clicked but nothing happened yet" — not "apply these in order some seconds
        // later." A single dropped click on a double-click feels right; the opposite
        // (surprise commands arriving after a long render) does not.
        if (_viewModel.IsExecutingCommand)
            return;

        // Entity placement: discrete left-click spawns one entity at the tile center
        // of the grid under the cursor. No drag, no right-click behavior yet (deletion
        // via Ctrl+Z for now — right-click-to-delete is a follow-up).
        if (_viewModel.SelectedEntity != null && props.IsLeftButtonPressed)
        {
            var hit = HitTestGrid(ScreenToWorldPixel(e.GetPosition(this)));
            if (hit == null) return;

            var tile = hit.Value.Tile;
            var localCoords = new System.Numerics.Vector2(tile.X + 0.5f, tile.Y + 0.5f);
            var cmd = new PlaceEntityCommand(
                hit.Value.Grid.GridUid!.Value,
                _viewModel.SelectedEntity.Id,
                localCoords);
            _ = _viewModel.ExecuteCommandAsync(cmd);
            e.Handled = true;
            return;
        }

        // Right-click for the inspector menu — only when no tile tool is active (tile
        // mode's right-click-to-erase falls through to the paint path below). Arm now,
        // open the menu on release so future right-drag gestures aren't blocked.
        if (props.IsRightButtonPressed && _viewModel.SelectedTile == null)
        {
            _rightClickArmed = true;
            _rightClickStart = e.GetPosition(this);
            e.Handled = true;
            return;
        }

        // Tile paint: requires a tile selection.
        if (_viewModel.SelectedTile == null)
            return;

        if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
        {
            var hit = HitTestGrid(ScreenToWorldPixel(e.GetPosition(this)));
            if (hit == null) return;

            _painting = true;
            _eraseStroke = props.IsRightButtonPressed;
            _strokeGridUid = hit.Value.Grid.GridUid!.Value;
            _strokeCoords.Clear();
            _strokeEdits.Clear();

            AddStrokeCell(hit.Value.Tile);
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        if (_panning)
        {
            _panOffset = _panStartOffset + (position - _panStartPointer);
            InvalidateVisual();
            return;
        }

        // Track hover for the ghost preview — only draw it when a tile is selected.
        var hit = HitTestGrid(ScreenToWorldPixel(position));
        if (hit == null)
        {
            if (_hasHover) { _hasHover = false; InvalidateVisual(); }
        }
        else
        {
            var changed = !_hasHover || !ReferenceEquals(_hoverGrid, hit.Value.Grid) || !_hoverTile.Equals(hit.Value.Tile);
            _hasHover = true;
            _hoverGrid = hit.Value.Grid;
            _hoverTile = hit.Value.Tile;
            if (changed) InvalidateVisual();
        }

        if (_painting && hit != null && hit.Value.Grid.GridUid == _strokeGridUid)
            AddStrokeCell(hit.Value.Tile);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_painting)
        {
            _painting = false;
            e.Pointer.Capture(null);
            e.Handled = true;

            // Commit the stroke as one undoable command. Skip on empty (shouldn't happen
            // given we always add at least the start cell on press, but be defensive).
            if (_strokeEdits.Count > 0 && _viewModel != null)
            {
                var cmd = new SetTilesCommand(_strokeGridUid, _strokeEdits);
                _ = _viewModel.ExecuteCommandAsync(cmd);
            }

            _strokeCoords.Clear();
            _strokeEdits.Clear();
            return;
        }

        // Right-click release: only fire the context menu if the pointer barely moved
        // since press (so a drift-to-drag gesture in the future stays non-menu).
        if (_rightClickArmed)
        {
            _rightClickArmed = false;
            var pos = e.GetPosition(this);
            var drift = pos - _rightClickStart;
            if (Math.Abs(drift.X) <= RightClickDragThreshold &&
                Math.Abs(drift.Y) <= RightClickDragThreshold)
            {
                _ = ShowEntityContextMenuAsync(pos);
                e.Handled = true;
            }
        }
    }

    /// <summary>Hit-test the clicked tile, ask the VM for entities on it, and pop up a
    /// fresh <see cref="ContextMenu"/> if any are there. Silently bails on empty hits so
    /// right-clicking empty space doesn't flash a menu.</summary>
    private async System.Threading.Tasks.Task ShowEntityContextMenuAsync(Point pointerPos)
    {
        if (_viewModel == null)
            return;

        var hit = HitTestGrid(ScreenToWorldPixel(pointerPos));
        if (hit == null || hit.Value.Grid.GridUid is not { } gridUid)
            return;

        IReadOnlyList<InspectedEntity> entities;
        try
        {
            entities = await _viewModel.GetEntitiesAtTileAsync(gridUid, hit.Value.Tile);
        }
        catch
        {
            return;
        }

        if (entities.Count == 0)
            return;

        // Build the menu fresh every time — Avalonia is finicky about re-showing a
        // ContextMenu whose items were swapped out, and the entity list is per-click.
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Pointer,
        };

        foreach (var ent in entities)
        {
            // Capture the uid in a local so the handler lambda closes over the right one.
            var uid = ent.Uid;
            var header = $"{ent.Name}  —  {ent.PrototypeId}";
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => _ = _viewModel.SelectEntityAsync(uid);
            menu.Items.Add(item);
        }

        menu.Open(this);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hasHover) { _hasHover = false; InvalidateVisual(); }
    }

    private void AddStrokeCell(Vector2i coord)
    {
        if (!_strokeCoords.Add(coord)) return;
        if (_viewModel?.SelectedTile == null) return;

        // Erase = empty tile (TypeId 0). Paint = selected tile's numeric TypeId.
        var newTile = _eraseStroke
            ? Tile.Empty
            : new Tile((ushort)_viewModel.SelectedTile.TypeId);

        _strokeEdits.Add((coord, newTile));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_document == null) return;

        var pointer = e.GetPosition(this);
        var worldBeforeX = (pointer.X - _panOffset.X) / _zoom;
        var worldBeforeY = (pointer.Y - _panOffset.Y) / _zoom;

        var factor = e.Delta.Y > 0 ? ZoomStep : 1.0 / ZoomStep;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);

        _panOffset = new Vector(
            pointer.X - worldBeforeX * _zoom,
            pointer.Y - worldBeforeY * _zoom);

        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        if (_renderedGrids.Count == 0)
            return;

        foreach (var (grid, bitmap) in _renderedGrids)
        {
            var gridPixelX = grid.WorldOffset.X * PixelsPerTile;
            var gridPixelY = grid.WorldOffset.Y * PixelsPerTile;

            var destX = gridPixelX * _zoom + _panOffset.X;
            var destY = gridPixelY * _zoom + _panOffset.Y;
            var destW = grid.PixelWidth  * _zoom;
            var destH = grid.PixelHeight * _zoom;

            if (destX + destW < 0 || destY + destH < 0 ||
                destX > Bounds.Width || destY > Bounds.Height)
                continue;

            context.DrawImage(
                bitmap,
                new Rect(0, 0, grid.PixelWidth, grid.PixelHeight),
                new Rect(destX, destY, destW, destH));
        }

        // Selection highlight: outline the tile under SelectedMapEntity.LocalCoords,
        // on whichever grid it's parented to. Drawn before the hover so the hover
        // ghost (if present on the same tile) still wins visually — hover is tied
        // to the pointer and should be the louder signal.
        if (_viewModel?.SelectedMapEntity is { } sel)
        {
            var rect = TryGetEntityTileScreenRect(sel);
            if (rect.HasValue)
                context.DrawRectangle(SelectionOutlinePen, rect.Value);
        }

        // Hover ghost: semi-transparent fill + thin outline on the hovered tile.
        if (_hasHover && _hoverGrid != null && _viewModel?.SelectedTile != null)
        {
            var rect = HoverTileScreenRect(_hoverGrid, _hoverTile);
            context.FillRectangle(HoverFillBrush, rect);
            context.DrawRectangle(HoverOutlinePen, rect);
        }
    }

    /// <summary>Locate the <see cref="RenderedGrid"/> the inspected entity is parented
    /// to and project its grid-local coords onto a tile rectangle in screen space.
    /// Returns null if the grid isn't currently rendered (it got disposed, or the
    /// entity's parent isn't a grid we know about).</summary>
    private Rect? TryGetEntityTileScreenRect(InspectedEntity entity)
    {
        foreach (var (grid, _) in _renderedGrids)
        {
            if (grid.GridUid != entity.ParentGridUid)
                continue;

            // LocalCoords is a Vector2 in grid-local world-space — floor to the tile
            // that owns it. Matches the convention EntityInspectionService uses when
            // it builds the world-AABB around Vector2i tile coords.
            var tile = new Vector2i(
                (int)Math.Floor(entity.LocalCoords.X),
                (int)Math.Floor(entity.LocalCoords.Y));
            return HoverTileScreenRect(grid, tile);
        }
        return null;
    }

    /// <summary>Screen-space rectangle of the given grid-local tile coord, accounting
    /// for the vertical flip applied during render.</summary>
    private Rect HoverTileScreenRect(RenderedGrid grid, Vector2i tile)
    {
        // Grid-local tile coord → canvas-local tile (pre-flip) → canvas-local tile (post-flip)
        // → world pixel → screen.
        var canvasTileX = tile.X - grid.TileMin.X;
        var canvasTileY = tile.Y - grid.TileMin.Y;
        var canvasTileYFlipped = grid.TileCount.Y - 1 - canvasTileY;

        var worldPxX = grid.WorldOffset.X * PixelsPerTile + canvasTileX * PixelsPerTile;
        var worldPxY = grid.WorldOffset.Y * PixelsPerTile + canvasTileYFlipped * PixelsPerTile;

        var screenX = worldPxX * _zoom + _panOffset.X;
        var screenY = worldPxY * _zoom + _panOffset.Y;
        var screenSize = PixelsPerTile * _zoom;

        return new Rect(screenX, screenY, screenSize, screenSize);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        DisposeBitmaps();
        base.OnDetachedFromVisualTree(e);
    }

    private void DisposeBitmaps()
    {
        foreach (var (_, bitmap) in _renderedGrids)
            bitmap.Dispose();
        _renderedGrids.Clear();
    }
}
