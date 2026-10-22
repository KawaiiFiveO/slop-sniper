using SkiaSharp;

namespace SlopSniper.Services;

/// <summary>Decodes untrusted images on a small VPS: the first frame only, scaled down while decoding where the format
/// allows, and refused outright if it claims more pixels than the bot is willing to hold.</summary>
public static class ImageDecoder
{
    // 16 megapixels is 64 MB as RGBA; larger images are rejected before anything is allocated.
    private const long MaxPixels = 16_000_000;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>The image scaled down to fit in <paramref name="maxSide"/> pixels, or null if it isn't an image Skia
    /// can decode, or is too large.</summary>
    public static SKBitmap? TryDecode(ReadOnlySpan<byte> data, int maxSide)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            return null;
        }

        // JPEG and WebP can decode straight to a smaller size; other formats come back full size.
        var size = codec.GetScaledDimensions(Math.Min(1f, (float)maxSide / Math.Max(codec.Info.Width, codec.Info.Height)));
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }

        if (Math.Max(bitmap.Width, bitmap.Height) <= maxSide)
        {
            return bitmap;
        }

        var scale = (double)maxSide / Math.Max(bitmap.Width, bitmap.Height);
        using (bitmap)
        {
            return Resize(bitmap, Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
        }
    }

    /// <summary>A copy at exactly this size, stretched if the aspect ratio differs.</summary>
    public static SKBitmap Resize(SKBitmap bitmap, int width, int height) =>
        bitmap.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), Sampling);

    /// <summary>Perceived brightness, 0 to 255.</summary>
    public static double Luminance(SKColor color) => 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;
}
