using System.Text;

namespace Etiq.Core;

/// <summary>
/// Dependency-free QR encoder (ISO/IEC 18004, model 2): versions 1-40,
/// ECC L/M/Q/H, numeric / alphanumeric / byte modes (mode picked
/// automatically, smallest fitting version picked automatically), full
/// Reed-Solomon over GF(256), all 8 masks with standard penalty scoring.
/// Output is the module matrix WITHOUT quiet zone — renderers add ≥4
/// modules of quiet zone. Verified by decoding with zbar and zxing-cpp.
/// </summary>
public static class QrCode
{
    /// <summary>Encode content at the given ECC level ('L','M','Q','H').
    /// Returns the module matrix (true = dark), or null when the content
    /// does not fit version 40 at that level. minVersion floors the symbol
    /// size (logo overlays force ≥2 so the center keepout stays a safe
    /// fraction of the codewords).</summary>
    public static bool[,]? Encode(string content, char ecc = 'M', int minVersion = 1)
    {
        int ecIdx = "LMQH".IndexOf(char.ToUpperInvariant(ecc));
        if (ecIdx < 0) ecIdx = 1;
        minVersion = Math.Clamp(minVersion, 1, 40);

        int mode = PickMode(content);
        byte[] byteData = mode == 2 ? Encoding.UTF8.GetBytes(content) : Array.Empty<byte>();
        int charCount = mode == 2 ? byteData.Length : content.Length;

        // smallest version whose data capacity fits mode header + payload
        int version = -1, dataCodewords = 0;
        for (int v = minVersion; v <= 40; v++)
        {
            int idx = (v - 1) * 4 + ecIdx;
            int dcw = QrTables.Blocks[idx * 5 + 1] * QrTables.Blocks[idx * 5 + 2]
                    + QrTables.Blocks[idx * 5 + 3] * QrTables.Blocks[idx * 5 + 4];
            int bits = 4 + CountBits(mode, v) + PayloadBits(mode, charCount);
            if (bits <= dcw * 8) { version = v; dataCodewords = dcw; break; }
        }
        if (version < 0) return null;

        // --- bit stream: mode, count, payload, terminator, pad ---
        var bw = new BitWriter();
        bw.Append(mode switch { 0 => 0b0001, 1 => 0b0010, _ => 0b0100 }, 4);
        bw.Append(charCount, CountBits(mode, version));
        switch (mode)
        {
            case 0: // numeric: groups of 3 digits -> 10 bits (2->7, 1->4)
                for (int i = 0; i < content.Length; i += 3)
                {
                    int n = Math.Min(3, content.Length - i);
                    bw.Append(int.Parse(content.Substring(i, n)), n * 3 + 1);
                }
                break;
            case 1: // alphanumeric: pairs -> 11 bits (single -> 6)
                for (int i = 0; i < content.Length; i += 2)
                {
                    int a = Alnum.IndexOf(content[i]);
                    if (i + 1 < content.Length)
                        bw.Append(a * 45 + Alnum.IndexOf(content[i + 1]), 11);
                    else bw.Append(a, 6);
                }
                break;
            default:
                foreach (var b in byteData) bw.Append(b, 8);
                break;
        }
        int capacityBits = dataCodewords * 8;
        bw.Append(0, Math.Min(4, capacityBits - bw.Length));     // terminator
        while (bw.Length % 8 != 0) bw.Append(0, 1);
        for (int p = 0; bw.Length < capacityBits; p ^= 1)        // pad bytes
            bw.Append(p == 0 ? 0xEC : 0x11, 8);

        // --- split into blocks, add RS ecc, interleave ---
        int bi = ((version - 1) * 4 + ecIdx) * 5;
        int ecPerBlock = QrTables.Blocks[bi];
        int g1c = QrTables.Blocks[bi + 1], g1d = QrTables.Blocks[bi + 2];
        int g2c = QrTables.Blocks[bi + 3], g2d = QrTables.Blocks[bi + 4];
        var data = bw.ToBytes();
        var blocks = new List<byte[]>();
        var eccBlocks = new List<byte[]>();
        int off = 0;
        var gen = Gf256.GeneratorPoly(ecPerBlock);
        for (int i = 0; i < g1c + g2c; i++)
        {
            int len = i < g1c ? g1d : g2d;
            var blk = data.AsSpan(off, len).ToArray();
            off += len;
            blocks.Add(blk);
            eccBlocks.Add(Gf256.RsEncode(blk, gen, ecPerBlock));
        }
        var seq = new List<byte>();
        int maxD = Math.Max(g1d, g2d);
        for (int i = 0; i < maxD; i++)
            foreach (var blk in blocks)
                if (i < blk.Length) seq.Add(blk[i]);
        for (int i = 0; i < ecPerBlock; i++)
            foreach (var eb in eccBlocks) seq.Add(eb[i]);

        // --- matrix: function patterns, data placement, best mask ---
        int size = 17 + 4 * version;
        var (grid, isFunc) = BuildFunctionPatterns(version, size);
        PlaceData(grid, isFunc, seq, size);

        int bestMask = 0;
        long bestPenalty = long.MaxValue;
        bool[,]? best = null;
        for (int m = 0; m < 8; m++)
        {
            var g = (bool[,])grid.Clone();
            ApplyMask(g, isFunc, m, size);
            WriteFormat(g, size, ecIdx, m);
            if (version >= 7) WriteVersion(g, size, version);
            long p = Penalty(g, size);
            if (p < bestPenalty) { bestPenalty = p; bestMask = m; best = g; }
        }
        _ = bestMask;
        return best;
    }

    private const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private static int PickMode(string s)
    {
        if (s.Length > 0 && s.All(char.IsAsciiDigit)) return 0;
        if (s.Length > 0 && s.All(c => Alnum.Contains(c))) return 1;
        return 2;
    }

    private static int CountBits(int mode, int version) => mode switch
    {
        0 => version < 10 ? 10 : version < 27 ? 12 : 14,
        1 => version < 10 ? 9 : version < 27 ? 11 : 13,
        _ => version < 10 ? 8 : 16,
    };

    private static int PayloadBits(int mode, int n) => mode switch
    {
        0 => n / 3 * 10 + (n % 3 == 2 ? 7 : n % 3 == 1 ? 4 : 0),
        1 => n / 2 * 11 + n % 2 * 6,
        _ => n * 8,
    };

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        public int Length { get; private set; }
        public void Append(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (Length % 8 == 0) _bytes.Add(0);
                if ((value >> i & 1) != 0)
                    _bytes[^1] |= (byte)(0x80 >> Length % 8);
                Length++;
            }
        }
        public byte[] ToBytes() => _bytes.ToArray();
    }

    // ---------- function patterns ----------

    private static (bool[,] Grid, bool[,] IsFunc) BuildFunctionPatterns(int version, int size)
    {
        var grid = new bool[size, size];
        var isFunc = new bool[size, size];
        void Set(int x, int y, bool dark)
        {
            grid[y, x] = dark;
            isFunc[y, x] = true;
        }
        void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(x, y, d != 4 && d != 2);
                }
        }
        Finder(3, 3);
        Finder(size - 4, 3);
        Finder(3, size - 4);
        // timing
        for (int i = 8; i < size - 8; i++)
        {
            if (!isFunc[6, i]) Set(i, 6, i % 2 == 0);
            if (!isFunc[i, 6]) Set(6, i, i % 2 == 0);
        }
        // alignment: all center combinations EXCEPT the three that overlap
        // finders — centers on the timing row/col (e.g. (6,22) at v7) are
        // real patterns drawn over the timing line
        var pos = QrTables.Align[version - 1];
        int last = pos.Length - 1;
        for (int ai = 0; ai <= last; ai++)
            for (int aj = 0; aj <= last; aj++)
            {
                if ((ai == 0 && aj == 0) || (ai == 0 && aj == last) || (ai == last && aj == 0))
                    continue;
                int cy = pos[ai], cx = pos[aj];
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        // format info areas (reserved; written after masking) + dark module
        for (int i = 0; i < 8; i++)
        {
            isFunc[8, i] = isFunc[i, 8] = true;   // top-left row/col strips
            isFunc[8, size - 1 - i] = true;       // second copy, row 8 right: bits 0..7 (8 cells)
        }
        for (int i = 0; i < 7; i++)
            isFunc[size - 1 - i, 8] = true;       // second copy, col 8 bottom: bits 8..14 (7 cells)
        isFunc[8, 8] = true;
        Set(8, size - 8, true);   // dark module
        if (version >= 7)
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 3; j++)
                {
                    isFunc[i, size - 11 + j] = true;
                    isFunc[size - 11 + j, i] = true;
                }
        return (grid, isFunc);
    }

    private static void PlaceData(bool[,] grid, bool[,] isFunc, List<byte> codewords, int size)
    {
        int bit = 0, total = codewords.Count * 8;
        bool upward = true;
        for (int col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--;   // skip the vertical timing column
            for (int i = 0; i < size; i++)
            {
                int y = upward ? size - 1 - i : i;
                for (int dx = 0; dx < 2; dx++)
                {
                    int x = col - dx;
                    if (isFunc[y, x]) continue;
                    bool dark = bit < total &&
                        (codewords[bit / 8] >> (7 - bit % 8) & 1) != 0;
                    grid[y, x] = dark;
                    bit++;
                }
            }
            upward = !upward;
        }
    }

    private static void ApplyMask(bool[,] g, bool[,] isFunc, int mask, int size)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (isFunc[y, x]) continue;
                bool flip = mask switch
                {
                    0 => (y + x) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (y + x) % 3 == 0,
                    4 => (y / 2 + x / 3) % 2 == 0,
                    5 => y * x % 2 + y * x % 3 == 0,
                    6 => (y * x % 2 + y * x % 3) % 2 == 0,
                    _ => ((y + x) % 2 + y * x % 3) % 2 == 0,
                };
                if (flip) g[y, x] = !g[y, x];
            }
    }

    private static void WriteFormat(bool[,] g, int size, int ecIdx, int mask)
    {
        int data = "LMQH"[ecIdx] switch { 'L' => 0b01, 'M' => 0b00, 'Q' => 0b11, _ => 0b10 };
        int bits = data << 3 | mask;
        int rem = bits << 10;
        for (int i = 14; i >= 10; i--)
            if ((rem >> i & 1) != 0) rem ^= 0b10100110111 << (i - 10);
        int format = (bits << 10 | rem) ^ 0b101010000010010;

        for (int i = 0; i <= 5; i++) g[i, 8] = Bit(format, i);   // col 8, rows 0-5
        g[7, 8] = Bit(format, 6);
        g[8, 8] = Bit(format, 7);
        g[8, 7] = Bit(format, 8);
        for (int i = 9; i <= 14; i++) g[8, 14 - i] = Bit(format, i);   // row 8, cols 5-0
        for (int i = 0; i <= 7; i++) g[8, size - 1 - i] = Bit(format, i);       // row 8, right
        for (int i = 8; i <= 14; i++) g[size - 15 + i, 8] = Bit(format, i);     // col 8, bottom
        static bool Bit(int v, int i) => (v >> i & 1) != 0;
    }

    private static void WriteVersion(bool[,] g, int size, int version)
    {
        int rem = version << 12;
        for (int i = 17; i >= 12; i--)
            if ((rem >> i & 1) != 0) rem ^= 0b1111100100101 << (i - 12);
        int bits = version << 12 | rem;
        for (int i = 0; i < 18; i++)
        {
            bool b = (bits >> i & 1) != 0;
            g[i / 3, size - 11 + i % 3] = b;
            g[size - 11 + i % 3, i / 3] = b;
        }
    }

    private static long Penalty(bool[,] g, int size)
    {
        long p = 0;
        // rule 1: runs of >=5 in rows and columns
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? g[a, b] : g[b, a];
                    bool prev = pass == 0 ? g[a, b - 1] : g[b - 1, a];
                    if (cur == prev) run++;
                    else { if (run >= 5) p += 3 + (run - 5); run = 1; }
                }
                if (run >= 5) p += 3 + (run - 5);
            }
        // rule 2: 2x2 blocks of one color
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
                if (g[y, x] == g[y, x + 1] && g[y, x] == g[y + 1, x] && g[y, x] == g[y + 1, x + 1])
                    p += 3;
        // rule 3: finder-like 1:1:3:1:1 with 4 light modules on either side
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int bits = 0;
                for (int b = 0; b < size; b++)
                {
                    bits = (bits << 1 | ((pass == 0 ? g[a, b] : g[b, a]) ? 1 : 0)) & 0x7FF;
                    if (b >= 10 && (bits == 0b10111010000 || bits == 0b00001011101))
                        p += 40;
                }
            }
        // rule 4: dark ratio deviation from 50%
        long dark = 0;
        foreach (bool d in g) if (d) dark++;
        long pct = dark * 100 / (size * (long)size);
        p += Math.Abs(pct - 50) / 5 * 10;
        return p;
    }
}

/// <summary>GF(256) Reed-Solomon shared by QR and DataMatrix. Both use
/// 8-bit symbols; QR's field polynomial is 0x11D, DataMatrix's is 0x12D —
/// instantiate per field.</summary>
internal sealed class Gf256Field
{
    public readonly byte[] Exp = new byte[512];
    public readonly byte[] Log = new byte[256];
    public Gf256Field(int poly, int generatorBase = 2)
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= poly;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        _ = generatorBase;
    }
    public byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
}

/// <summary>QR's RS arithmetic (field poly 0x11D, generator roots x^i from i=0).</summary>
internal static class Gf256
{
    private static readonly Gf256Field F = new(0x11D);

    /// <summary>Generator polynomial coefficients for n ecc symbols
    /// (monic, low-order last).</summary>
    public static byte[] GeneratorPoly(int n) => GeneratorPoly(F, n, 0);

    public static byte[] GeneratorPoly(Gf256Field f, int n, int firstRoot)
    {
        var g = new byte[] { 1 };
        for (int i = 0; i < n; i++)
        {
            var ng = new byte[g.Length + 1];
            byte root = f.Exp[(i + firstRoot) % 255];
            for (int j = 0; j < g.Length; j++)
            {
                ng[j] ^= f.Mul(g[j], root);
                ng[j + 1] ^= g[j];
            }
            // ng built low-to-high; normalize orientation: shift so ng[0] is x^len
            g = ng;
        }
        // reverse to high-order-first for the division loop
        Array.Reverse(g);
        return g;
    }

    public static byte[] RsEncode(byte[] data, byte[] gen, int eccLen) =>
        RsEncode(F, data, gen, eccLen);

    /// <summary>Polynomial long division remainder = the ecc symbols.</summary>
    public static byte[] RsEncode(Gf256Field f, byte[] data, byte[] gen, int eccLen)
    {
        var rem = new byte[eccLen];
        foreach (var d in data)
        {
            byte factor = (byte)(d ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, eccLen - 1);
            rem[eccLen - 1] = 0;
            if (factor == 0) continue;
            for (int i = 0; i < eccLen; i++)
                rem[i] ^= f.Mul(gen[i + 1], factor);
        }
        return rem;
    }
}
