using System.Text;

namespace Etiq.Core;

/// <summary>
/// Dependency-free PDF417 encoder (ISO/IEC 15438): byte compaction (works
/// for any content; 6 bytes → 5 codewords base 900), Reed-Solomon over
/// GF(929) at security levels 0-8 (auto-picked from data length), row
/// indicators, and the low-level 17-module bar patterns from the three
/// cluster tables. Output is the module matrix with ONE matrix row per
/// symbol row — renderers stretch rows to ~3 module heights. Verified by
/// decoding with zxing-cpp.
/// </summary>
public static class Pdf417
{
    private const int Start = 0x1FEA8;   // 17 modules
    private const int Stop = 0x3FA29;    // 18 modules
    private const int Pad = 900;

    /// <summary>Encode content. columns 1-30 (data columns per row);
    /// security -1 = auto by data length. Returns the module matrix
    /// (true = dark; one matrix row per symbol row) or null when the data
    /// cannot fit the size limits.</summary>
    public static bool[,]? Encode(string content, int columns = 6, int security = -1)
    {
        if (columns is < 1 or > 30) columns = 6;
        var bytes = Encoding.UTF8.GetBytes(content);

        // --- byte compaction ---
        var data = new List<int> { bytes.Length % 6 == 0 ? 924 : 901 };
        for (int i = 0; i < bytes.Length; i += 6)
        {
            int n = Math.Min(6, bytes.Length - i);
            if (n == 6)
            {
                // base 256 -> base 900: 6 bytes become exactly 5 codewords
                ulong v = 0;
                for (int j = 0; j < 6; j++) v = v << 8 | bytes[i + j];
                var five = new int[5];
                for (int j = 4; j >= 0; j--)
                {
                    five[j] = (int)(v % 900);
                    v /= 900;
                }
                data.AddRange(five);
            }
            else
                for (int j = 0; j < n; j++) data.Add(bytes[i + j]);
        }

        // --- security level: spec-recommended by data codeword count ---
        if (security is < 0 or > 8)
            security = data.Count switch
            {
                <= 40 => 2, <= 160 => 3, <= 320 => 4, _ => 5,
            };
        int eccCount = 1 << (security + 1);

        // pad so (1 + data + pad + ecc) fills whole rows
        int total = 1 + data.Count + eccCount;
        int padCount = (columns - total % columns) % columns;
        int lengthDescriptor = 1 + data.Count + padCount;
        int rows = (total + padCount) / columns;
        if (lengthDescriptor > 928 || rows > 90) return null;
        while (rows < 3) { rows++; padCount += columns; lengthDescriptor += columns; }

        var words = new List<int> { lengthDescriptor };
        words.AddRange(data);
        for (int i = 0; i < padCount; i++) words.Add(Pad);

        // --- Reed-Solomon over GF(929) ---
        var factors = Pdf417Tables.EcFactors[security];
        var ec = new int[eccCount];
        foreach (var w in words)
        {
            int temp = (w + ec[^1]) % 929;
            for (int x = eccCount - 1; x >= 0; x--)
            {
                int prev = x > 0 ? ec[x - 1] : 0;
                ec[x] = (prev + 929 - temp * factors[x] % 929) % 929;
            }
        }
        for (int i = 0; i < eccCount; i++)
            if (ec[i] > 0) ec[i] = 929 - ec[i];
        for (int i = eccCount - 1; i >= 0; i--) words.Add(ec[i]);

        // --- rows: start + left indicator + data + right indicator + stop ---
        int widthModules = 17 /*start*/ + (columns + 2) * 17 + 18 /*stop*/;
        var outp = new bool[rows, widthModules];
        for (int r = 0; r < rows; r++)
        {
            int k = r % 3;
            int left = 30 * (r / 3) + k switch
            {
                0 => (rows - 1) / 3,
                1 => security * 3 + (rows - 1) % 3,
                _ => columns - 1,
            };
            int right = 30 * (r / 3) + k switch
            {
                0 => columns - 1,
                1 => (rows - 1) / 3,
                _ => security * 3 + (rows - 1) % 3,
            };
            var cluster = k == 0 ? Pdf417Tables.Cluster0
                        : k == 1 ? Pdf417Tables.Cluster1 : Pdf417Tables.Cluster2;
            int x = 0;
            void Emit(int pattern, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                    outp[r, x++] = (pattern >> i & 1) != 0;
            }
            Emit(Start, 17);
            Emit(cluster[left], 17);
            for (int c = 0; c < columns; c++)
                Emit(cluster[words[r * columns + c]], 17);
            Emit(cluster[right], 17);
            Emit(Stop, 18);
        }
        return outp;
    }
}
