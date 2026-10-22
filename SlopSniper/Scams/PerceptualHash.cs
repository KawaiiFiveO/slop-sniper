using System.Numerics;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>64-bit perceptual hash (pHash): the image shrunk to 32×32 grayscale, its lowest 8×8 DCT frequencies
/// compared to their median. Resizing, recompressing and small edits barely change it, so near-identical images
/// are a few bits apart while unrelated ones are around 32.</summary>
public static class PerceptualHash
{
    private const int Size = 32;
    private const int Frequencies = 8;

    // Rows of the DCT-II basis for the frequencies used: Cos[u, x] = cos((2x + 1) u π / 2N).
    private static readonly double[,] Cos = BuildCosTable();

    /// <returns>The hash, or null if the data isn't a decodable image.</returns>
    public static ulong? Compute(ReadOnlySpan<byte> image)
    {
        using var decoded = ImageDecoder.TryDecode(image, Size * 2);
        if (decoded is null)
        {
            return null;
        }

        // Stretched rather than padded, so the same image at a different aspect ratio still lines up.
        using var small = ImageDecoder.Resize(decoded, Size, Size);
        var pixels = new double[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                pixels[y, x] = ImageDecoder.Luminance(small.GetPixel(x, y));
            }
        }

        var coefficients = LowFrequencyDct(pixels);

        // The DC term (overall brightness) is left out of the median so it doesn't skew it.
        var median = coefficients.Skip(1).Order().ElementAt((coefficients.Length - 1) / 2);
        ulong hash = 0;
        for (var i = 0; i < coefficients.Length; i++)
        {
            if (coefficients[i] > median)
            {
                hash |= 1UL << i;
            }
        }
        return hash;
    }

    /// <summary>Number of differing bits, 0 to 64.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>The top-left 8×8 of the 2D DCT, row by row.</summary>
    private static double[] LowFrequencyDct(double[,] pixels)
    {
        // Rows first: rowDct[y, u] = Σx pixels[y, x] · Cos[u, x].
        var rowDct = new double[Size, Frequencies];
        for (var y = 0; y < Size; y++)
        {
            for (var u = 0; u < Frequencies; u++)
            {
                double sum = 0;
                for (var x = 0; x < Size; x++)
                {
                    sum += pixels[y, x] * Cos[u, x];
                }
                rowDct[y, u] = sum;
            }
        }

        var result = new double[Frequencies * Frequencies];
        for (var v = 0; v < Frequencies; v++)
        {
            for (var u = 0; u < Frequencies; u++)
            {
                double sum = 0;
                for (var y = 0; y < Size; y++)
                {
                    sum += rowDct[y, u] * Cos[v, y];
                }
                result[v * Frequencies + u] = sum;
            }
        }
        return result;
    }

    private static double[,] BuildCosTable()
    {
        var table = new double[Frequencies, Size];
        for (var u = 0; u < Frequencies; u++)
        {
            for (var x = 0; x < Size; x++)
            {
                table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Size));
            }
        }
        return table;
    }
}
