namespace Etiq.Core;

/// <summary>
/// Aztec Code encoder (ISO/IEC 24778): compact (1-4 layers) and full
/// (1-32 layers) symbols, binary-shift data encodation (mode-agnostic —
/// any Latin-1 content), bit stuffing, Reed-Solomon over GF(2^b) with the
/// standard field polynomials per word size (6/8/10/12 bits), GF(16) mode
/// message, zxing-compatible layer/reference-grid construction. No quiet
/// zone required by the spec (finder is central). Decode-verified with
/// zxing-cpp (68/68 incl. fuzz up to 800 bytes / 101x101 full symbols).
/// </summary>
public static class Aztec
{
    /// <summary>Encode content (Latin-1). Null when it cannot fit the
    /// largest symbol or contains characters beyond Latin-1.</summary>
    public static bool[,]? Encode(string content)
    {
        var data = new byte[content.Length];
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] > 255) return null;
            data[i] = (byte)content[i];
        }

        // --- binary-shift bit stream: from UPPER mode, B/S(31) + length ---
        var bits = new List<bool>();
        void Emit(int val, int n)
        {
            for (int i = n - 1; i >= 0; i--) bits.Add((val >> i & 1) != 0);
        }
        for (int i = 0; i < data.Length; )
        {
            int chunk = Math.Min(2078, data.Length - i);
            Emit(31, 5);
            if (chunk <= 31) Emit(chunk, 5);
            else { Emit(0, 5); Emit(chunk - 31, 11); }
            for (int j = 0; j < chunk; j++) Emit(data[i + j], 8);
            i += chunk;
        }

        // --- pick the smallest symbol: stuffed bits + 33%+11 ecc must fit ---
        int eccBits = bits.Count * 33 / 100 + 11;
        bool compact = false; int layers = 0; List<bool>? stuffed = null; int tb = int.MaxValue;
        foreach (bool comp in new[] { true, false })
        {
            for (int l = 1; l <= (comp ? 4 : 32); l++)
            {
                int b = WordSize(l);
                int cap = TotalBits(l, comp);
                var st = Stuff(bits, b);
                if (st.Count + eccBits <= cap && st.Count / b <= cap / b - 3)
                {
                    if (cap < tb) { compact = comp; layers = l; stuffed = st; tb = cap; }
                    break;   // smallest layer for this family found
                }
            }
        }
        if (stuffed is null) return null;

        // --- data words + RS check words over GF(2^b) ---
        int bw = WordSize(layers);
        int totalWords = tb / bw;
        int dataWords = stuffed.Count / bw;
        var words = new int[dataWords];
        for (int i = 0; i < dataWords; i++)
        {
            int w = 0;
            for (int j = 0; j < bw; j++) w = w << 1 | (stuffed[i * bw + j] ? 1 : 0);
            words[i] = w;
        }
        var gf = new GfInt(bw);
        var ecc = gf.Rs(words, totalWords - dataWords);
        var msg = new List<bool>();
        for (int i = 0; i < tb % bw; i++) msg.Add(false);   // start padding
        foreach (int w in Concat(words, ecc))
            for (int j = bw - 1; j >= 0; j--) msg.Add((w >> j & 1) != 0);

        // --- mode message: layers-1 + dataWords-1, RS over GF(16) ---
        var gf4 = new GfInt(4);
        int[] mwords;
        if (compact)
        {
            int md = (layers - 1) << 6 | (dataWords - 1);
            var dw = new[] { md >> 4 & 15, md & 15 };
            mwords = Concat(dw, gf4.Rs(dw, 5));
        }
        else
        {
            int md = (layers - 1) << 11 | (dataWords - 1);
            var dw = new[] { md >> 12 & 15, md >> 8 & 15, md >> 4 & 15, md & 15 };
            mwords = Concat(dw, gf4.Rs(dw, 6));
        }
        var mbits = new List<bool>();
        foreach (int w in mwords)
            for (int j = 3; j >= 0; j--) mbits.Add((w >> j & 1) != 0);

        // --- matrix construction (zxing-compatible) ---
        int baseSize = (compact ? 11 : 14) + layers * 4;
        int size;
        int[] amap;
        if (compact)
        {
            size = baseSize;
            amap = new int[baseSize];
            for (int i = 0; i < baseSize; i++) amap[i] = i;
        }
        else
        {
            size = baseSize + 1 + 2 * ((baseSize / 2 - 1) / 15);
            amap = new int[baseSize];
            int oc = baseSize / 2, c0 = size / 2;
            for (int i = 0; i < oc; i++)
            {
                int off = i + i / 15;
                amap[oc - i - 1] = c0 - off - 1;
                amap[oc + i] = c0 + off + 1;
            }
        }
        var m = new bool[size, size];
        void Set(int x, int y) => m[y, x] = true;

        // data layers, spiraling: left column / bottom row / right / top
        int rowOff = 0;
        for (int i = 0; i < layers; i++)
        {
            int rowSize = (layers - i) * 4 + (compact ? 9 : 12);
            for (int j = 0; j < rowSize; j++)
            {
                int colOff = j * 2;
                for (int k = 0; k < 2; k++)
                {
                    if (msg[rowOff + colOff + k])
                        Set(amap[i * 2 + k], amap[i * 2 + j]);
                    if (msg[rowOff + rowSize * 2 + colOff + k])
                        Set(amap[i * 2 + j], amap[baseSize - 1 - i * 2 - k]);
                    if (msg[rowOff + rowSize * 4 + colOff + k])
                        Set(amap[baseSize - 1 - i * 2 - k], amap[baseSize - 1 - i * 2 - j]);
                    if (msg[rowOff + rowSize * 6 + colOff + k])
                        Set(amap[baseSize - 1 - i * 2 - j], amap[i * 2 + k]);
                }
            }
            rowOff += rowSize * 8;
        }

        int c = size / 2;
        // mode message ring
        if (compact)
        {
            for (int i = 0; i < 7; i++)
            {
                int off = c - 3 + i;
                if (mbits[i]) Set(off, c - 5);
                if (mbits[i + 7]) Set(c + 5, off);
                if (mbits[20 - i]) Set(off, c + 5);
                if (mbits[27 - i]) Set(c - 5, off);
            }
        }
        else
        {
            for (int i = 0; i < 10; i++)
            {
                int off = c - 5 + i + i / 5;
                if (mbits[i]) Set(off, c - 7);
                if (mbits[i + 10]) Set(c + 7, off);
                if (mbits[29 - i]) Set(off, c + 7);
                if (mbits[39 - i]) Set(c - 7, off);
            }
        }
        // reference grid (full symbols)
        if (!compact)
        {
            for (int i = 0, j = 0; i < baseSize / 2 - 1; i += 15, j += 16)
                for (int k = c & 1; k < size; k += 2)
                {
                    Set(c - j, k); Set(c + j, k);
                    Set(k, c - j); Set(k, c + j);
                }
        }
        // bulls-eye + the six orientation marks
        int bs = compact ? 5 : 7;
        for (int i = 0; i < bs; i += 2)
            for (int j = c - i; j <= c + i; j++)
            {
                Set(j, c - i); Set(j, c + i);
                Set(c - i, j); Set(c + i, j);
            }
        Set(c - bs, c - bs);
        Set(c - bs + 1, c - bs);
        Set(c - bs, c - bs + 1);
        Set(c + bs, c - bs);
        Set(c + bs, c - bs + 1);
        Set(c + bs, c + bs - 1);
        return m;
    }

    private static int WordSize(int layers) =>
        layers <= 2 ? 6 : layers <= 8 ? 8 : layers <= 22 ? 10 : 12;

    private static int TotalBits(int layers, bool compact) =>
        ((compact ? 88 : 112) + 16 * layers) * layers;

    /// <summary>Bit stuffing: consume b bits (missing bits count as 1);
    /// a word whose top b-1 bits are all equal gets its last bit forced to
    /// the complement, and one input bit is pushed back.</summary>
    private static List<bool> Stuff(List<bool> bits, int b)
    {
        var outp = new List<bool>();
        int mask = (1 << b) - 2;
        for (int i = 0; i < bits.Count; i += b)
        {
            int word = 0;
            for (int j = 0; j < b; j++)
                if (i + j >= bits.Count || bits[i + j]) word |= 1 << (b - 1 - j);
            int w;
            if ((word & mask) == mask) { w = word & mask; i--; }
            else if ((word & mask) == 0) { w = word | 1; i--; }
            else w = word;
            for (int j = b - 1; j >= 0; j--) outp.Add((w >> j & 1) != 0);
        }
        return outp;
    }

    private static int[] Concat(int[] a, int[] bArr)
    {
        var r = new int[a.Length + bArr.Length];
        a.CopyTo(r, 0);
        bArr.CopyTo(r, a.Length);
        return r;
    }

    /// <summary>Small-field GF(2^bits) Reed-Solomon (Aztec uses 4/6/8/10/12
    /// bit symbols; Gf256 is byte-only). Generator roots α^1..α^n.</summary>
    private sealed class GfInt
    {
        private readonly int _size;
        private readonly int[] _exp;
        private readonly int[] _log;
        private static readonly Dictionary<int, int> Poly = new()
            { [4] = 0x13, [6] = 0x43, [8] = 0x12D, [10] = 0x409, [12] = 0x1069 };

        public GfInt(int bits)
        {
            _size = 1 << bits;
            _exp = new int[2 * _size];
            _log = new int[_size];
            int poly = Poly[bits], x = 1;
            for (int i = 0; i < _size - 1; i++)
            {
                _exp[i] = x; _log[x] = i;
                x <<= 1;
                if (x >= _size) x ^= poly;
            }
            for (int i = _size - 1; i < 2 * _size; i++) _exp[i] = _exp[i - (_size - 1)];
        }

        private int Mul(int a, int b) => a == 0 || b == 0 ? 0 : _exp[_log[a] + _log[b]];

        public int[] Rs(int[] data, int necc)
        {
            var g = new List<int> { 1 };
            for (int i = 1; i <= necc; i++)
            {
                int root = _exp[i];
                var ng = new int[g.Count + 1];
                for (int j = 0; j < g.Count; j++)
                {
                    ng[j] ^= g[j];
                    ng[j + 1] ^= Mul(g[j], root);
                }
                g = new List<int>(ng);
            }
            var res = new int[data.Length + necc];
            data.CopyTo(res, 0);
            for (int i = 0; i < data.Length; i++)
            {
                int coef = res[i];
                if (coef == 0) continue;
                for (int j = 1; j < g.Count; j++)
                    res[i + j] ^= Mul(g[j], coef);
            }
            var ecc = new int[necc];
            Array.Copy(res, data.Length, ecc, 0, necc);
            return ecc;
        }
    }
}
