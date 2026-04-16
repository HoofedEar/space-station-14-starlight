#nullable enable
using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RobustMapEditor.Core.Tiles;
using RobustMapEditor.Ui.Rendering;

namespace RobustMapEditor.Ui.ViewModels;

/// <summary>
/// One entry in the tile palette panel. Owns an Avalonia <see cref="Bitmap"/>
/// converted once from the Core <see cref="TileCatalogEntry"/>'s ImageSharp
/// thumbnail, plus a name + selection flag.
/// </summary>
public sealed partial class TilePaletteItem : ObservableObject, IDisposable
{
    /// <summary>Backing catalog entry. Holds the engine-side TypeId needed to
    /// build <see cref="Robust.Shared.Map.Tile"/> values when the user paints.</summary>
    public TileCatalogEntry Entry { get; }

    public string Name => Entry.Name;
    public string Id => Entry.Id;
    public int TypeId => Entry.TypeId;
    public bool IsEmpty => Entry.IsEmpty;

    /// <summary>UI-facing thumbnail. Null for the space / eraser entry, in which case
    /// the view displays a placeholder.</summary>
    public Bitmap? Thumbnail { get; }

    [ObservableProperty]
    private bool _isSelected;

    public TilePaletteItem(TileCatalogEntry entry)
    {
        Entry = entry;
        if (entry.Thumbnail != null)
            Thumbnail = ImageSharpBridge.ToAvaloniaBitmap(entry.Thumbnail);
    }

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Entry.Dispose();
    }
}
