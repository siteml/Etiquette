namespace Etiq.Core;

/// <summary>
/// Dependency-free DataMatrix ECC200 encoder (ISO/IEC 16022): square
/// symbols 10x10 … 144x144 AND the six rectangular formats 8x18 … 16x48,
/// ASCII encodation (digit pairs compacted, extended ASCII via Upper
/// Shift), Reed-Solomon over GF(256) with the DataMatrix field polynomial
/// 0x12D, block interleaving for large symbols, and the standard ECC200
/// module placement (utah + corner cases — the Annex F reference algorithm
/// is row/column-generic, so rectangles reuse it unchanged). Output is the
/// module matrix WITHOUT quiet zone (spec quiet zone = 1 module minimum;
/// renderers add ≥2). Verified against the ppf.datamatrix reference and by
/// decoding with zxing-cpp (all rectangular sizes decode-verified too).
/// </summary>
public static class DataMatrix
{
    private static readonly Gf256Field F = new(0x12D);

    // rows, cols, mapRows/mapCols (per data region), vRegions, hRegions,
    // dataCW, eccCW, blocks
    private static readonly int[,] Square =
    {
        { 10, 10,  8,  8, 1, 1,    3,   5,  1 },
        { 12, 12, 10, 10, 1, 1,    5,   7,  1 },
        { 14, 14, 12, 12, 1, 1,    8,  10,  1 },
        { 16, 16, 14, 14, 1, 1,   12,  12,  1 },
        { 18, 18, 16, 16, 1, 1,   18,  14,  1 },
        { 20, 20, 18, 18, 1, 1,   22,  18,  1 },
        { 22, 22, 20, 20, 1, 1,   30,  20,  1 },
        { 24, 24, 22, 22, 1, 1,   36,  24,  1 },
        { 26, 26, 24, 24, 1, 1,   44,  28,  1 },
        { 32, 32, 14, 14, 2, 2,   62,  36,  1 },
        { 36, 36, 16, 16, 2, 2,   86,  42,  1 },
        { 40, 40, 18, 18, 2, 2,  114,  48,  1 },
        { 44, 44, 20, 20, 2, 2,  144,  56,  1 },
        { 48, 48, 22, 22, 2, 2,  174,  68,  1 },
        { 52, 52, 24, 24, 2, 2,  204,  84,  2 },
        { 64, 64, 14, 14, 4, 4,  280, 112,  2 },
        { 72, 72, 16, 16, 4, 4,  368, 144,  4 },
        { 80, 80, 18, 18, 4, 4,  456, 192,  4 },
        { 88, 88, 20, 20, 4, 4,  576, 224,  4 },
        { 96, 96, 22, 22, 4, 4,  696, 272,  4 },
        {104,104, 24, 24, 4, 4,  816, 336,  6 },
        {120,120, 18, 18, 6, 6, 1050, 408,  6 },
        {132,132, 20, 20, 6, 6, 1304, 496,  8 },
        {144,144, 22, 22, 6, 6, 1558, 620, 10 },
    };

    // ISO 16022 rectangular formats — short and wide, made for skinny
    // label real estate. All single RS block.
    private static readonly int[,] Rect =
    {
        {  8, 18,  6, 16, 1, 1,   5,  7, 1 },
        {  8, 32,  6, 14, 1, 2,  10, 11, 1 },
        { 12, 26, 10, 24, 1, 1,  16, 14, 1 },
        { 12, 36, 10, 16, 1, 2,  22, 18, 1 },
        { 16, 36, 14, 16, 1, 2,  32, 24, 1 },
        { 16, 48, 14, 22, 1, 2,  49, 28, 1 },
    };

    /// <summary>Encode content (Latin-1 range; other chars fail) into the
    /// smallest SQUARE symbol. Returns the module matrix (true = dark) or
    /// null when it doesn't fit 144x144 or contains non-encodable chars.</summary>
    public static bool[,]? Encode(string content) => Encode(content, preferRect: false);

    /// <summary>preferRect: use a RECTANGULAR symbol (8x18 … 16x48) when
    /// the content fits one (≤49 data codewords); content too long for any
    /// rectangle silently falls back to a square symbol.</summary>
    public static bool[,]? Encode(string content, bool preferRect) =>
        Encode(content, preferRect, 0);

    /// <summary>targetAspect (width/height of the destination box, ≤0 =
    /// ignore): among the rectangles the content fits, pick the one whose
    /// cols/rows ratio best matches the box — a wide skinny box gets 8x32
    /// (4.0) over 12x26 (2.17). With no aspect the smallest fitting
    /// rectangle wins, as before.</summary>
    public static bool[,]? Encode(string content, bool preferRect, double targetAspect)
    {
        var cw = Encodation(content);
        if (cw is null) return null;
        if (preferRect && BestRect(cw, targetAspect) is { } r) return r;
        return Build(cw, Square);
    }

    private static bool[,]? BestRect(List<byte> data, double targetAspect)
    {
        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = 0; i < Rect.GetLength(0); i++)
        {
            if (data.Count > Rect[i, 6]) continue;
            // no aspect: first fit (table is smallest-first). With aspect:
            // log-ratio distance plus a small size penalty, so a marginal
            // aspect win never buys a needlessly bigger symbol (smaller
            // symbol = bigger modules = better scans). Verified picks:
            // AB@2.0→8x18, AB@4.0→8x32, 14ch@2.2→12x26, 14ch@3.2→12x36.
            double score = targetAspect <= 0
                ? i
                : Math.Abs(Math.Log((double)Rect[i, 1] / Rect[i, 0] / targetAspect)) + 0.05 * i;
            if (score < bestScore - 1e-9) { bestScore = score; best = i; }
        }
        return best < 0 ? null : BuildAt(data, Rect, best);
    }

    // --- ASCII encodation ---
    private static List<byte>? Encodation(string content)
    {
        var cw = new List<byte>();
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (char.IsAsciiDigit(c) && i + 1 < content.Length && char.IsAsciiDigit(content[i + 1]))
            {
                cw.Add((byte)(130 + (c - '0') * 10 + (content[i + 1] - '0')));
                i++;
            }
            else if (c <= 127) cw.Add((byte)(c + 1));
            else if (c <= 255) { cw.Add(235); cw.Add((byte)(c - 128 + 1)); }
            else return null;   // beyond Latin-1: not supported
        }
        return cw;
    }

    /// <summary>Pick the smallest symbol in the table, pad, compute RS ecc,
    /// place, and assemble the finder borders. Null when nothing fits.</summary>
    private static bool[,]? Build(List<byte> data, int[,] t)
    {
        for (int i = 0; i < t.GetLength(0); i++)
            if (data.Count <= t[i, 6]) return BuildAt(data, t, i);
        return null;
    }

    private static bool[,] BuildAt(List<byte> data, int[,] t, int si)
    {
        int rows = t[si, 0], cols = t[si, 1], mapR = t[si, 2], mapC = t[si, 3],
            vRegs = t[si, 4], hRegs = t[si, 5],
            dataCap = t[si, 6], eccTotal = t[si, 7], blocks = t[si, 8];

        // --- padding: 129 then 253-state randomized pads ---
        var cw = new List<byte>(data);
        if (cw.Count < dataCap)
        {
            cw.Add(129);
            while (cw.Count < dataCap)
            {
                int pad = 129 + (149 * (cw.Count + 1) % 253 + 1);
                if (pad > 254) pad -= 254;
                cw.Add((byte)pad);
            }
        }

        // --- RS ecc, interleaved blocks (codeword i -> block i % blocks).
        // 144x144 special case: blocks 0-7 have 156 data cw, blocks 8-9 have
        // 155 — the round-robin handles it because 1558 = 8*156 + 2*155. ---
        int eccPerBlock = eccTotal / blocks;
        var gen = Gf256.GeneratorPoly(F, eccPerBlock, 1);
        var ecc = new byte[eccTotal];
        for (int b = 0; b < blocks; b++)
        {
            var blk = new List<byte>();
            for (int i = b; i < cw.Count; i += blocks) blk.Add(cw[i]);
            var e = Gf256.RsEncode(F, blk.ToArray(), gen, eccPerBlock);
            for (int i = 0; i < eccPerBlock; i++) ecc[b + i * blocks] = e[i];
        }
        var all = new List<byte>(cw);
        all.AddRange(ecc);

        // --- place into the mapping matrix, then add finder borders ---
        var map = PlaceEcc200(all, mapR * vRegs, mapC * hRegs);

        var outp = new bool[rows, cols];
        for (int ry = 0; ry < vRegs; ry++)
            for (int rx = 0; rx < hRegs; rx++)
            {
                int ox = rx * (mapC + 2), oy = ry * (mapR + 2);
                for (int i = 0; i < mapC + 2; i++)
                {
                    outp[oy + mapR + 1, ox + i] = true;           // solid bottom
                    outp[oy, ox + i] = i % 2 == 0;                // dashed top
                }
                for (int i = 0; i < mapR + 2; i++)
                {
                    outp[oy + i, ox] = true;                      // solid left
                    outp[oy + i, ox + mapC + 1] = i % 2 == 1;     // dashed right
                }
                for (int y = 0; y < mapR; y++)
                    for (int x = 0; x < mapC; x++)
                        outp[oy + 1 + y, ox + 1 + x] =
                            map[ry * mapR + y, rx * mapC + x];
            }
        return outp;
    }

    /// <summary>The ECC200 bit placement (ISO 16022 Annex F reference
    /// algorithm): utah shapes walking diagonals, four special corner
    /// conditions, and the fixed 2x2 checker when the corner stays empty.</summary>
    private static bool[,] PlaceEcc200(List<byte> cw, int nrow, int ncol)
    {
        var mat = new bool[nrow, ncol];
        var used = new bool[nrow, ncol];

        void Module(int row, int col, int idx, int bit)
        {
            if (row < 0) { row += nrow; col += 4 - (nrow + 4) % 8; }
            if (col < 0) { col += ncol; row += 4 - (ncol + 4) % 8; }
            mat[row, col] = (cw[idx] >> (8 - bit) & 1) != 0;
            used[row, col] = true;
        }
        void Utah(int row, int col, int idx)
        {
            Module(row - 2, col - 2, idx, 1);
            Module(row - 2, col - 1, idx, 2);
            Module(row - 1, col - 2, idx, 3);
            Module(row - 1, col - 1, idx, 4);
            Module(row - 1, col, idx, 5);
            Module(row, col - 2, idx, 6);
            Module(row, col - 1, idx, 7);
            Module(row, col, idx, 8);
        }
        void Corner1(int idx)
        {
            Module(nrow - 1, 0, idx, 1);
            Module(nrow - 1, 1, idx, 2);
            Module(nrow - 1, 2, idx, 3);
            Module(0, ncol - 2, idx, 4);
            Module(0, ncol - 1, idx, 5);
            Module(1, ncol - 1, idx, 6);
            Module(2, ncol - 1, idx, 7);
            Module(3, ncol - 1, idx, 8);
        }
        void Corner2(int idx)
        {
            Module(nrow - 3, 0, idx, 1);
            Module(nrow - 2, 0, idx, 2);
            Module(nrow - 1, 0, idx, 3);
            Module(0, ncol - 4, idx, 4);
            Module(0, ncol - 3, idx, 5);
            Module(0, ncol - 2, idx, 6);
            Module(0, ncol - 1, idx, 7);
            Module(1, ncol - 1, idx, 8);
        }
        void Corner3(int idx)
        {
            Module(nrow - 3, 0, idx, 1);
            Module(nrow - 2, 0, idx, 2);
            Module(nrow - 1, 0, idx, 3);
            Module(0, ncol - 2, idx, 4);
            Module(0, ncol - 1, idx, 5);
            Module(1, ncol - 1, idx, 6);
            Module(2, ncol - 1, idx, 7);
            Module(3, ncol - 1, idx, 8);
        }
        void Corner4(int idx)
        {
            Module(nrow - 1, 0, idx, 1);
            Module(nrow - 1, ncol - 1, idx, 2);
            Module(0, ncol - 3, idx, 3);
            Module(0, ncol - 2, idx, 4);
            Module(0, ncol - 1, idx, 5);
            Module(1, ncol - 3, idx, 6);
            Module(1, ncol - 2, idx, 7);
            Module(1, ncol - 1, idx, 8);
        }

        int pos = 0, row0 = 4, col0 = 0;
        do
        {
            if (row0 == nrow && col0 == 0) Corner1(pos++);
            if (row0 == nrow - 2 && col0 == 0 && ncol % 4 != 0) Corner2(pos++);
            if (row0 == nrow - 2 && col0 == 0 && ncol % 8 == 4) Corner3(pos++);
            if (row0 == nrow + 4 && col0 == 2 && ncol % 8 == 0) Corner4(pos++);
            do
            {
                if (row0 < nrow && col0 >= 0 && !used[row0, col0]) Utah(row0, col0, pos++);
                row0 -= 2;
                col0 += 2;
            } while (row0 >= 0 && col0 < ncol);
            row0 += 1;
            col0 += 3;
            do
            {
                if (row0 >= 0 && col0 < ncol && !used[row0, col0]) Utah(row0, col0, pos++);
                row0 += 2;
                col0 -= 2;
            } while (row0 < nrow && col0 >= 0);
            row0 += 3;
            col0 += 1;
        } while (row0 < nrow || col0 < ncol);

        // fixed checker in the bottom-right corner when unfilled
        if (!used[nrow - 1, ncol - 1])
        {
            mat[nrow - 1, ncol - 1] = mat[nrow - 2, ncol - 2] = true;
        }
        return mat;
    }
}
