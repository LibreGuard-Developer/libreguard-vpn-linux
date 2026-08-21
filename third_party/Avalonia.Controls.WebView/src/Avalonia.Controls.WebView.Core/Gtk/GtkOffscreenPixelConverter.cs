using System;

namespace Avalonia.Controls.Gtk;

internal static class GtkOffscreenPixelConverter
{
    internal static void ConvertRgbaToPremultipliedBgra(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int destinationStride,
        int width,
        int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        var rowLength = checked(width * 4);
        if (sourceStride < rowLength)
            throw new ArgumentOutOfRangeException(nameof(sourceStride));
        if (destinationStride < rowLength)
            throw new ArgumentOutOfRangeException(nameof(destinationStride));

        var requiredSourceLength = RequiredLength(sourceStride, rowLength, height);
        var requiredDestinationLength = RequiredLength(destinationStride, rowLength, height);
        if (source.Length < requiredSourceLength)
            throw new ArgumentException("The source buffer is smaller than the requested image.", nameof(source));
        if (destination.Length < requiredDestinationLength)
            throw new ArgumentException("The destination buffer is smaller than the requested image.", nameof(destination));

        for (var y = 0; y < height; y++)
        {
            var sourceRow = source.Slice(y * sourceStride, rowLength);
            var destinationRow = destination.Slice(y * destinationStride, rowLength);
            for (var x = 0; x < rowLength; x += 4)
            {
                var alpha = sourceRow[x + 3];
                destinationRow[x] = Premultiply(sourceRow[x + 2], alpha);
                destinationRow[x + 1] = Premultiply(sourceRow[x + 1], alpha);
                destinationRow[x + 2] = Premultiply(sourceRow[x], alpha);
                destinationRow[x + 3] = alpha;
            }
        }
    }

    private static int RequiredLength(int stride, int rowLength, int height)
        => height == 0 ? 0 : checked(((height - 1) * stride) + rowLength);

    private static byte Premultiply(byte color, byte alpha)
        => (byte)(((color * alpha) + 127) / 255);
}
