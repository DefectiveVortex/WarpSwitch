using System;
using System.Collections.Generic;
using System.Text;

namespace WarpSwitch;

/// Minimal QR code encoder (byte mode, versions 1–40), enough to show the hotspot's "scan to join" code without
/// pulling in a library. Follows the ISO/IEC 18004 algorithm as laid out in Project Nayuki's reference encoder.
sealed class QrCode
{
    public enum Ecc { Low, Medium, Quartile, High }

    public readonly int Version, Size;
    readonly Ecc ecc;
    readonly bool[,] modules;    // [y, x], true = dark
    bool[,] isFunction;

    public bool this[int x, int y] => modules[y, x];

    public static QrCode Encode(string text, Ecc ecc = Ecc.Medium) => Encode(Encoding.UTF8.GetBytes(text), ecc);

    public static QrCode Encode(byte[] data, Ecc ecc)
    {
        int version, usedBits;
        for (version = 1; ; version++)
        {
            int countBits = version <= 9 ? 8 : 16;
            usedBits = 4 + countBits + data.Length * 8;
            if (data.Length < 1 << countBits && usedBits <= DataCodewords(version, ecc) * 8) break;
            if (version == 40) throw new ArgumentException("Too much data for a QR code.");
        }
        // Use the strongest error correction that still fits in this size.
        foreach (Ecc stronger in new[] { Ecc.Medium, Ecc.Quartile, Ecc.High })
            if (stronger > ecc && usedBits <= DataCodewords(version, stronger) * 8) ecc = stronger;

        var bits = new List<bool>();
        Append(bits, 0b0100, 4);   // byte mode
        Append(bits, data.Length, version <= 9 ? 8 : 16);
        foreach (byte b in data) Append(bits, b, 8);
        int capacity = DataCodewords(version, ecc) * 8;
        Append(bits, 0, Math.Min(4, capacity - bits.Count));
        Append(bits, 0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);

        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return new QrCode(version, ecc, codewords);
    }

    static void Append(List<bool> bits, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    QrCode(int version, Ecc ecc, byte[] dataCodewords)
    {
        Version = version;
        Size = version * 4 + 17;
        this.ecc = ecc;
        modules = new bool[Size, Size];
        isFunction = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(dataCodewords));

        int best = 0, bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            int penalty = Penalty();
            if (penalty < bestPenalty) { best = mask; bestPenalty = penalty; }
            ApplyMask(mask);   // XOR again = undo
        }
        ApplyMask(best);
        DrawFormatBits(best);
        isFunction = null;
    }

    // ---------------------------------------------------------------- function patterns

    void SetFunction(int x, int y, bool dark)
    {
        modules[y, x] = dark;
        isFunction[y, x] = true;
    }

    void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);   // timing patterns
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);
        var align = AlignmentPositions();
        int n = align.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                    DrawAlignment(align[i], align[j]);
        DrawFormatBits(0);   // reserve the area; real bits come once the mask is chosen
        DrawVersion();
    }

    void DrawFinder(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy)), xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, dist != 2 && dist != 4);
            }
    }

    void DrawAlignment(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    int[] AlignmentPositions()
    {
        if (Version == 1) return new int[0];
        int count = Version / 7 + 2;
        int step = (Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    static int FormatBitsOf(Ecc e) => e switch { Ecc.Low => 1, Ecc.Medium => 0, Ecc.Quartile => 3, _ => 2 };

    void DrawFormatBits(int mask)
    {
        int data = FormatBitsOf(ecc) << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);   // always dark
    }

    void DrawVersion()
    {
        if (Version < 7) return;
        int rem = Version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int bits = Version << 12 | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    static bool Bit(int x, int i) => ((x >> i) & 1) != 0;

    // ---------------------------------------------------------------- data + error correction

    byte[] AddEccAndInterleave(byte[] data)
    {
        int blocks = EccBlocks[(int)ecc, Version], eccLen = EccPerBlock[(int)ecc, Version];
        int raw = RawDataModules(Version) / 8;
        int shortBlocks = blocks - raw % blocks, shortLen = raw / blocks;
        var divisor = RsDivisor(eccLen);
        var all = new byte[blocks][];
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int len = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var dat = new byte[len];
            Array.Copy(data, k, dat, 0, len);
            k += len;
            var block = new byte[shortLen + 1];
            Array.Copy(dat, block, len);
            var e = RsRemainder(dat, divisor);
            Array.Copy(e, 0, block, block.Length - eccLen, eccLen);
            all[i] = block;
        }
        var result = new byte[raw];
        for (int i = 0, k = 0; i < all[0].Length; i++)
            for (int j = 0; j < blocks; j++)
                if (i != shortLen - eccLen || j >= shortBlocks)   // short blocks have no byte here
                    result[k++] = all[j][i];
        return result;
    }

    void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;   // skip the vertical timing column
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!isFunction[y, x] && i < data.Length * 8)
                    {
                        modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
        }
    }

    void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !isFunction[y, x]) modules[y, x] = !modules[y, x];
            }
    }

    /// The standard's mask penalty: long runs, 2x2 blocks, finder look-alikes, and dark/light imbalance.
    int Penalty()
    {
        int result = 0, dark = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < Size; a++)
            {
                int run = 0;
                bool last = false;
                for (int b = 0; b < Size; b++)
                {
                    bool m = pass == 0 ? modules[a, b] : modules[b, a];
                    if (b > 0 && m == last) run++;
                    else
                    {
                        if (run >= 5) result += run - 2;
                        run = 1;
                        last = m;
                    }
                    if (b >= 10 && FinderLike(pass, a, b)) result += 40;
                }
                if (run >= 5) result += run - 2;
            }
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = modules[y, x];
                if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1]) result += 3;
            }
        foreach (bool m in modules) if (m) dark++;
        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return result + k * 10;
    }

    static readonly bool[] Pattern1 = { true, false, true, true, true, false, true, false, false, false, false };
    static readonly bool[] Pattern2 = { false, false, false, false, true, false, true, true, true, false, true };

    bool FinderLike(int pass, int a, int end)
    {
        bool p1 = true, p2 = true;
        for (int i = 0; i < 11; i++)
        {
            int b = end - 10 + i;
            bool m = pass == 0 ? modules[a, b] : modules[b, a];
            if (m != Pattern1[i]) p1 = false;
            if (m != Pattern2[i]) p2 = false;
        }
        return p1 || p2;
    }

    static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = (byte)RsMultiply(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }
            root = RsMultiply(root, 0x02);
        }
        return result;
    }

    static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[result.Length - 1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)RsMultiply(divisor[i], factor);
        }
        return result;
    }

    static int RsMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    static int RawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int align = ver / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    static int DataCodewords(int ver, Ecc e) => RawDataModules(ver) / 8 - EccPerBlock[(int)e, ver] * EccBlocks[(int)e, ver];

    static readonly int[,] EccPerBlock =
    {
        { -1,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    static readonly int[,] EccBlocks =
    {
        { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };
}
