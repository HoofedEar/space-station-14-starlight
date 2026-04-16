#nullable enable
using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RobustMapEditor.Core.Entities;
using RobustMapEditor.Ui.Rendering;

namespace RobustMapEditor.Ui.ViewModels;

/// <summary>
/// One entry in the entity palette panel. Parallel of <see cref="TilePaletteItem"/>
/// for entity prototypes; carries the prototype identifier and a thumbnail cropped
/// from the prototype's primary sprite layer.
/// </summary>
public sealed partial class EntityPaletteItem : ObservableObject, IDisposable
{
    public EntityCatalogEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string? EditorSuffix => Entry.EditorSuffix;

    /// <summary>Avalonia-side thumbnail. Null when the prototype's sprite couldn't be
    /// resolved; the view falls back to a placeholder indicator.</summary>
    public Bitmap? Thumbnail { get; }

    /// <summary>Displayed as a subtitle under the name. Prototype ID plus any editor
    /// suffix the prototype set (e.g. "TableCarpet (empty)").</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Entry.EditorSuffix)
        ? Entry.Id
        : $"{Entry.Id} {Entry.EditorSuffix}";

    public EntityPaletteItem(EntityCatalogEntry entry)
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
