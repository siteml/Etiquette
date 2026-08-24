namespace Etiq.Core;

/// <summary>
/// rMQR Code encoder (ISO/IEC 23941) — byte mode. Rectangular QR-family
/// symbols from R7x43 to R17x139: QR's GF(256)/0x11D Reed-Solomon (roots
/// from α^0), 18-bit BCH format information, fixed (y/2 + x/3) mask.
/// Algorithm and constant tables follow the MIT-licensed rmqrcode
/// reference (github.com/OUDON/rmqrcode-python) with two table entries
/// corrected against zint (R13x27-M: k=12, R17x43-M: c=61,k=39). All 32
/// versions × both ECC levels decode-verified with zxing-cpp.
/// </summary>
public static class Rmqr
{
    /// <summary>h, w, version indicator, remainder bits, byte-mode char
    /// count indicator bits, data bits (M), data bits (H), blocks (M),
    /// blocks (H). A block tuple is (count, totalCw, dataCw).</summary>
    private sealed record V(int H, int W, int Ver, int Rem, int Cci,
                            int BitsM, int BitsH,
                            (int N, int C, int K)[] BlocksM,
                            (int N, int C, int K)[] BlocksH);

    private static readonly V[] Versions =
    {
        new(7, 43, 0, 0, 3, 48, 24, new[]{(1,13,6)}, new[]{(1,13,3)}),
        new(7, 59, 1, 3, 4, 96, 56, new[]{(1,21,12)}, new[]{(1,21,7)}),
        new(7, 77, 2, 5, 5, 160, 80, new[]{(1,32,20)}, new[]{(1,32,10)}),
        new(7, 99, 3, 6, 5, 224, 112, new[]{(1,44,28)}, new[]{(1,44,14)}),
        new(7, 139, 4, 1, 6, 352, 192, new[]{(1,68,44)}, new[]{(2,34,12)}),
        new(9, 43, 5, 2, 4, 96, 56, new[]{(1,21,12)}, new[]{(1,21,7)}),
        new(9, 59, 6, 3, 5, 168, 88, new[]{(1,33,21)}, new[]{(1,33,11)}),
        new(9, 77, 7, 1, 5, 248, 136, new[]{(1,49,31)}, new[]{(1,24,8),(1,25,9)}),
        new(9, 99, 8, 4, 6, 336, 176, new[]{(1,66,42)}, new[]{(2,33,11)}),
        new(9, 139, 9, 5, 6, 504, 264, new[]{(1,49,31),(1,50,32)}, new[]{(3,33,11)}),
        new(11, 27, 10, 2, 3, 56, 40, new[]{(1,15,7)}, new[]{(1,15,5)}),
        new(11, 43, 11, 1, 5, 152, 88, new[]{(1,31,19)}, new[]{(1,31,11)}),
        new(11, 59, 12, 0, 5, 248, 120, new[]{(1,47,31)}, new[]{(1,23,7),(1,24,8)}),
        new(11, 77, 13, 2, 6, 344, 184, new[]{(1,67,43)}, new[]{(1,33,11),(1,34,12)}),
        new(11, 99, 14, 7, 6, 456, 232, new[]{(1,44,28),(1,45,29)}, new[]{(1,44,14),(1,45,15)}),
        new(11, 139, 15, 6, 7, 672, 336, new[]{(2,66,42)}, new[]{(3,44,14)}),
        new(13, 27, 16, 4, 4, 96, 56, new[]{(1,21,12)}, new[]{(1,21,7)}),
        new(13, 43, 17, 1, 5, 216, 104, new[]{(1,41,27)}, new[]{(1,41,13)}),
        new(13, 59, 18, 6, 6, 304, 160, new[]{(1,60,38)}, new[]{(2,30,10)}),
        new(13, 77, 19, 4, 6, 424, 232, new[]{(1,42,26),(1,43,27)}, new[]{(1,42,14),(1,43,15)}),
        new(13, 99, 20, 3, 7, 584, 280, new[]{(1,56,36),(1,57,37)}, new[]{(1,37,11),(2,38,12)}),
        new(13, 139, 21, 0, 7, 848, 432, new[]{(2,55,35),(1,56,36)}, new[]{(2,41,13),(2,42,14)}),
        new(15, 43, 22, 1, 6, 264, 120, new[]{(1,51,33)}, new[]{(1,25,7),(1,26,8)}),
        new(15, 59, 23, 4, 6, 384, 208, new[]{(1,74,48)}, new[]{(2,37,13)}),
        new(15, 77, 24, 6, 7, 536, 248, new[]{(1,51,33),(1,52,34)}, new[]{(2,34,10),(1,35,11)}),
        new(15, 99, 25, 7, 7, 704, 384, new[]{(2,68,44)}, new[]{(4,34,12)}),
        new(15, 139, 26, 2, 7, 1016, 552, new[]{(2,66,42),(1,67,43)}, new[]{(1,39,13),(4,40,14)}),
        new(17, 43, 27, 1, 6, 312, 168, new[]{(1,61,39)}, new[]{(1,30,10),(1,31,11)}),
        new(17, 59, 28, 2, 6, 448, 224, new[]{(2,44,28)}, new[]{(2,44,14)}),
        new(17, 77, 29, 0, 7, 624, 304, new[]{(2,61,39)}, new[]{(1,40,12),(2,41,13)}),
        new(17, 99, 30, 3, 7, 800, 448, new[]{(2,53,33),(1,54,34)}, new[]{(4,40,14)}),
        new(17, 139, 31, 4, 8, 1216, 608, new[]{(4,58,38)}, new[]{(2,38,12),(4,39,13)}),
    };

    private static readonly Dictionary<int, int[]> AlignCols = new()
    {
        [27] = Array.Empty<int>(),
        [43] = new[] { 21 },
        [59] = new[] { 19, 39 },
        [77] = new[] { 25, 51 },
        [99] = new[] { 23, 49, 75 },
        [139] = new[] { 27, 55, 83, 111 },
    };

    private const int MaskFinder = 0b011111101010110010;
    private const int MaskSub = 0b100000101001111011;

    /// <summary>Encode content (Latin-1) into an rMQR symbol. eccH: level H
    /// instead of M. targetAspect (box W/H, ≤0 = ignore): among fitting
    /// versions pick the one whose w/h best matches; otherwise smallest.
    /// Null when the content doesn't fit any version (M max 152 bytes).</summary>
    public static bool[,]? Encode(string content, bool eccH = false, double targetAspect = 0)
    {
        var data = new byte[content.Length];
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] > 255) return null;   // beyond Latin-1
            data[i] = (byte)content[i];
        }

        V? best = null;
        double bestScore = double.MaxValue;
        foreach (var v in Versions)
        {
            int dbits = eccH ? v.BitsH : v.BitsM;
            if (3 + v.Cci + 8 * data.Length > dbits) continue;
            int total = 0;
            foreach (var (n, c, _) in v.BlocksM) total += n * c;
            double score = targetAspect <= 0
                ? total
                : Math.Abs(Math.Log((double)v.W / v.H / targetAspect)) + 0.002 * total;
            if (score < bestScore - 1e-9) { bestScore = score; best = v; }
        }
        return best is null ? null : Build(best, data, eccH);
    }

    private static bool[,] Build(V v, byte[] data, bool eccH)
    {
        int dbits = eccH ? v.BitsH : v.BitsM;
        var blocks = eccH ? v.BlocksH : v.BlocksM;

        // --- byte-mode bit stream: 011 + count + bytes + terminator ---
        var bits = new List<bool>();
        void Emit(int val, int n)
        {
            for (int i = n - 1; i >= 0; i--) bits.Add((val >> i & 1) != 0);
        }
        Emit(0b011, 3);
        Emit(data.Length, v.Cci);
        foreach (var b in data) Emit(b, 8);
        if (bits.Count + 3 <= dbits) Emit(0, 3);

        // --- codewords + pad bytes ---
        var cw = new List<byte>();
        for (int i = 0; i < bits.Count; i += 8)
        {
            int w = 0;
            for (int j = 0; j < 8; j++)
                w = w << 1 | (i + j < bits.Count && bits[i + j] ? 1 : 0);
            cw.Add((byte)w);
        }
        int totalData = 0;
        foreach (var (n, _, k) in blocks) totalData += n * k;
        for (int i = 0; cw.Count < totalData; i++)
            cw.Add(i % 2 == 0 ? (byte)0b11101100 : (byte)0b00010001);

        // --- per-block RS (QR field, roots from α^0) + interleave ---
        var dataBlocks = new List<byte[]>();
        var eccBlocks = new List<byte[]>();
        int idx = 0;
        foreach (var (n, c, k) in blocks)
            for (int r = 0; r < n; r++)
            {
                var d = cw.GetRange(idx, k).ToArray();
                idx += k;
                dataBlocks.Add(d);
                eccBlocks.Add(Gf256.RsEncode(d, Gf256.GeneratorPoly(c - k), c - k));
            }
        var final = new List<byte>();
        int maxD = 0, maxE = 0;
        foreach (var d in dataBlocks) maxD = Math.Max(maxD, d.Length);
        foreach (var e in eccBlocks) maxE = Math.Max(maxE, e.Length);
        for (int i = 0; i < maxD; i++)
            foreach (var d in dataBlocks)
                if (i < d.Length) final.Add(d[i]);
        for (int i = 0; i < maxE; i++)
            foreach (var e in eccBlocks)
                if (i < e.Length) final.Add(e[i]);

        // --- module matrix; -1 = undefined (encoding region) ---
        int H = v.H, W = v.W;
        var m = new int[H, W];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) m[y, x] = -1;

        // finder (top-left) + separator
        for (int i = 0; i < 7; i++)
            for (int j = 0; j < 7; j++)
                m[i, j] = i is 0 or 6 || j is 0 or 6 ? 1 : 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) m[2 + i, 2 + j] = 1;
        for (int n2 = 0; n2 < 8; n2++)
        {
            if (n2 < H) m[n2, 7] = 0;
            if (H >= 9) m[7, n2] = 0;
        }
        // sub finder (bottom-right)
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                m[H - 1 - i, W - 1 - j] = i is 0 or 4 || j is 0 or 4 ? 1 : 0;
        m[H - 3, W - 3] = 1;
        // corner finders
        m[H - 1, 0] = m[H - 1, 1] = m[H - 1, 2] = 1;
        if (H >= 11) { m[H - 2, 0] = 1; m[H - 2, 1] = 0; }
        m[0, W - 1] = m[0, W - 2] = 1;
        m[1, W - 1] = 1; m[1, W - 2] = 0;
        // alignment patterns (top + bottom at the fixed columns)
        foreach (int cx in AlignCols[W])
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    int col = i is 0 or 2 || j is 0 or 2 ? 1 : 0;
                    m[i, cx + j - 1] = col;
                    m[H - 1 - i, cx + j - 1] = col;
                }
        // timing patterns
        for (int j = 0; j < W; j++)
        {
            int col = (j + 1) % 2 != 0 ? 1 : 0;
            if (m[0, j] < 0) m[0, j] = col;
            if (m[H - 1, j] < 0) m[H - 1, j] = col;
        }
        var vcols = new List<int> { 0, W - 1 };
        vcols.AddRange(AlignCols[W]);
        for (int i = 0; i < H; i++)
        {
            int col = (i + 1) % 2 != 0 ? 1 : 0;
            foreach (int j in vcols)
                if (m[i, j] < 0) m[i, j] = col;
        }

        // --- format information: 18-bit BCH, two masked placements ---
        int fi = v.Ver | (eccH ? 1 << 5 : 0);
        fi = Bch18(fi);
        int f1 = fi ^ MaskFinder;
        for (int n2 = 0; n2 < 18; n2++)
            m[1 + n2 % 5, 8 + n2 / 5] = f1 >> n2 & 1;
        int f2 = fi ^ MaskSub;
        for (int n2 = 0; n2 < 15; n2++)
            m[H - 6 + n2 % 5, W - 8 + n2 / 5] = f2 >> n2 & 1;
        m[H - 6, W - 5] = f2 >> 15 & 1;
        m[H - 6, W - 4] = f2 >> 16 & 1;
        m[H - 6, W - 3] = f2 >> 17 & 1;

        // --- symbol character placement (7.7.3) + mask ---
        var seq = new List<int>();
        foreach (var c in final)
            for (int b = 7; b >= 0; b--) seq.Add(c >> b & 1);
        int dy = -1, cx2 = W - 2, cy = H - 6, bi = 0, rem = v.Rem;
        var inRegion = new bool[H, W];
        while (true)
        {
            foreach (int x in new[] { cx2, cx2 - 1 })
            {
                if (m[cy, x] < 0)
                {
                    if (bi == seq.Count) { m[cy, x] = 0; inRegion[cy, x] = true; rem--; }
                    else { m[cy, x] = seq[bi++]; inRegion[cy, x] = true; }
                    if (bi == seq.Count && rem == 0) break;
                }
            }
            if (bi == seq.Count && rem == 0) break;
            if (dy < 0 && cy == 1) { cx2 -= 2; dy = 1; }
            else if (dy > 0 && cy == H - 2) { cx2 -= 2; dy = -1; }
            else cy += dy;
        }
        var outp = new bool[H, W];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int val = m[y, x];
                if (inRegion[y, x] && (y / 2 + x / 3) % 2 == 0) val ^= 1;
                outp[y, x] = val == 1;
            }
        return outp;
    }

    /// <summary>18-bit format info: 6 data bits + 12-bit BCH remainder
    /// (generator x^12+x^11+x^10+x^9+x^8+x^5+x^2+1).</summary>
    private static int Bch18(int data6)
    {
        const int g = 1 << 12 | 1 << 11 | 1 << 10 | 1 << 9 | 1 << 8 | 1 << 5 | 1 << 2 | 1;
        int t = data6 << 12;
        while (BitLen(t) >= 13) t ^= g << (BitLen(t) - 13);
        return data6 << 12 | t;
        static int BitLen(int x)
        {
            int n = 0;
            while (x > 0) { n++; x >>= 1; }
            return n;
        }
    }
}
