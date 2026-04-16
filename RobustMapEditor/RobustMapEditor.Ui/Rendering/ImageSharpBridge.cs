#nullable enable
using System;
using Avalonia;
using Avalonia.Platform;
using AvBitmap = Avalonia.Media.Imaging.WriteableBitmap;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RobustMapEditor.Ui.Rendering;

/// <summary>
/// Converts <see cref="Image{Rgba32}"/> from the Core rendering pipeline into
/// an Avalonia <see cref="WriteableBitmap"/> suitable for display. We skip the
/// PNG encode/decode roundtrip and copy raw pixels directly: station-sized
/// grids produce multi-megabyte PNGs and the encode cost is noticeable on map
/// open. ImageSharp's <c>Rgba32</c> layout matches Avalonia's
/// <see cref="PixelFormat.Rgba8888"/> byte-for-byte.
/// </summary>
internal static class ImageSharpBridge
{
    private static readonly Vector DefaultDpi = new(96, 96);

    public static AvBitmap ToAvaloniaBitmap(Image<Rgba32> source)
    {
        var size = new PixelSize(source.Width, source.Height);
        var bitmap = new AvBitmap(size, DefaultDpi, PixelFormat.Rgba8888, AlphaFormat.Unpremul);

        using (var framebuffer = bitmap.Lock())
        {
            // ImageSharp guarantees contiguous rows when we use DangerousTryGetSinglePixelMemory,
            // but that only succeeds for small images. For anything larger the rows may be
            // split across groups, so copy row-by-row using the managed pixel accessor.
            // Avalonia's framebuffer row stride can be larger than width*4 (alignment padding),
            // so we honor it rather than assuming packed rows.
            var rowBytes = source.Width * 4;
            source.ProcessPixelRows(accessor =>
            {
                unsafe
                {
                    var dst = (byte*)framebuffer.Address;
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var srcRow = accessor.GetRowSpan(y);
                        var dstRow = new Span<byte>(dst + y * framebuffer.RowBytes, rowBytes);
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(srcRow).CopyTo(dstRow);
                    }
                }
            });
        }

        return bitmap;
    }
}
