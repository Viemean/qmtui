using System.IO.Compression;
using System.Text;

namespace QmTui.Utils;

public static class QrcDecoder
{
    private const int DesEncrypt = 1;
    private const int DesDecrypt = 0;

    private static readonly int[][] Sbox =
    [
        [
            14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
            4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13
        ],
        [
            15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5,
            0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9
        ],
        [
            10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
            13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12
        ],
        [
            7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
            10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14
        ],
        [
            2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
            4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3
        ],
        [
            12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
            9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13
        ],
        [
            4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
            1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12
        ],
        [
            13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
            7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11
        ]
    ];

    private static readonly int[] KeyRndShift = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    private static readonly int[] KeyPermC =
    [
        56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35
    ];

    private static readonly int[] KeyPermD =
    [
        62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3
    ];

    private static readonly int[] KeyCompression =
    [
        13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1, 40, 51, 30, 36, 46,
        54, 29, 39, 50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31
    ];

    private static readonly byte[] QrcKey =
    [
        0x21, 0x40, 0x23, 0x29, 0x28, 0x2a, 0x24, 0x25, 0x31, 0x32, 0x33, 0x5a,
        0x58, 0x43, 0x21, 0x40, 0x21, 0x40, 0x23, 0x29, 0x28, 0x4e, 0x48, 0x4c
    ];

    private static readonly int[][][] CachedDecryptSchedule = TripleDesKeySetup(QrcKey, DesDecrypt);

    private static int Bitnum(ReadOnlySpan<byte> arr, int b, int c)
    {
        int byteIndex = (b / 32) * 4 + 3 - ((b % 32) / 8);
        int bitShift = 7 - (b % 8);
        int bit = (arr[byteIndex] >>> bitShift) & 1;
        return bit << c;
    }

    private static int BitnumIntr(int a, int b, int c) => (((a >>> (31 - b)) & 1) << c);

    private static int BitnumIntl(int a, int b, int c) => (int)((((uint)(a << b)) & 0x80000000u) >>> c);

    private static int SboxBit(int a) => (a & 32) | ((a & 31) >>> 1) | ((a & 1) << 4);

    private static (int S0, int S1) InitialPermutation(ReadOnlySpan<byte> input, int offset)
    {
        var slice = input.Slice(offset, 8);
        int s0 =
            Bitnum(slice, 57, 31) | Bitnum(slice, 49, 30) | Bitnum(slice, 41, 29) | Bitnum(slice, 33, 28) |
            Bitnum(slice, 25, 27) | Bitnum(slice, 17, 26) | Bitnum(slice, 9, 25) | Bitnum(slice, 1, 24) |
            Bitnum(slice, 59, 23) | Bitnum(slice, 51, 22) | Bitnum(slice, 43, 21) | Bitnum(slice, 35, 20) |
            Bitnum(slice, 27, 19) | Bitnum(slice, 19, 18) | Bitnum(slice, 11, 17) | Bitnum(slice, 3, 16) |
            Bitnum(slice, 61, 15) | Bitnum(slice, 53, 14) | Bitnum(slice, 45, 13) | Bitnum(slice, 37, 12) |
            Bitnum(slice, 29, 11) | Bitnum(slice, 21, 10) | Bitnum(slice, 13, 9) | Bitnum(slice, 5, 8) |
            Bitnum(slice, 63, 7) | Bitnum(slice, 55, 6) | Bitnum(slice, 47, 5) | Bitnum(slice, 39, 4) |
            Bitnum(slice, 31, 3) | Bitnum(slice, 23, 2) | Bitnum(slice, 15, 1) | Bitnum(slice, 7, 0);

        int s1 =
            Bitnum(slice, 56, 31) | Bitnum(slice, 48, 30) | Bitnum(slice, 40, 29) | Bitnum(slice, 32, 28) |
            Bitnum(slice, 24, 27) | Bitnum(slice, 16, 26) | Bitnum(slice, 8, 25) | Bitnum(slice, 0, 24) |
            Bitnum(slice, 58, 23) | Bitnum(slice, 50, 22) | Bitnum(slice, 42, 21) | Bitnum(slice, 34, 20) |
            Bitnum(slice, 26, 19) | Bitnum(slice, 18, 18) | Bitnum(slice, 10, 17) | Bitnum(slice, 2, 16) |
            Bitnum(slice, 60, 15) | Bitnum(slice, 52, 14) | Bitnum(slice, 44, 13) | Bitnum(slice, 36, 12) |
            Bitnum(slice, 28, 11) | Bitnum(slice, 20, 10) | Bitnum(slice, 12, 9) | Bitnum(slice, 4, 8) |
            Bitnum(slice, 62, 7) | Bitnum(slice, 54, 6) | Bitnum(slice, 46, 5) | Bitnum(slice, 38, 4) |
            Bitnum(slice, 30, 3) | Bitnum(slice, 22, 2) | Bitnum(slice, 14, 1) | Bitnum(slice, 6, 0);

        return (s0, s1);
    }

    private static void InversePermutation(int s0, int s1, Span<byte> output, int offset)
    {
        output[offset + 3] = (byte)(BitnumIntr(s1, 7, 7) | BitnumIntr(s0, 7, 6) | BitnumIntr(s1, 15, 5) | BitnumIntr(s0, 15, 4) | BitnumIntr(s1, 23, 3) | BitnumIntr(s0, 23, 2) | BitnumIntr(s1, 31, 1) | BitnumIntr(s0, 31, 0));
        output[offset + 2] = (byte)(BitnumIntr(s1, 6, 7) | BitnumIntr(s0, 6, 6) | BitnumIntr(s1, 14, 5) | BitnumIntr(s0, 14, 4) | BitnumIntr(s1, 22, 3) | BitnumIntr(s0, 22, 2) | BitnumIntr(s1, 30, 1) | BitnumIntr(s0, 30, 0));
        output[offset + 1] = (byte)(BitnumIntr(s1, 5, 7) | BitnumIntr(s0, 5, 6) | BitnumIntr(s1, 13, 5) | BitnumIntr(s0, 13, 4) | BitnumIntr(s1, 21, 3) | BitnumIntr(s0, 21, 2) | BitnumIntr(s1, 29, 1) | BitnumIntr(s0, 29, 0));
        output[offset + 0] = (byte)(BitnumIntr(s1, 4, 7) | BitnumIntr(s0, 4, 6) | BitnumIntr(s1, 12, 5) | BitnumIntr(s0, 12, 4) | BitnumIntr(s1, 20, 3) | BitnumIntr(s0, 20, 2) | BitnumIntr(s1, 28, 1) | BitnumIntr(s0, 28, 0));
        output[offset + 7] = (byte)(BitnumIntr(s1, 3, 7) | BitnumIntr(s0, 3, 6) | BitnumIntr(s1, 11, 5) | BitnumIntr(s0, 11, 4) | BitnumIntr(s1, 19, 3) | BitnumIntr(s0, 19, 2) | BitnumIntr(s1, 27, 1) | BitnumIntr(s0, 27, 0));
        output[offset + 6] = (byte)(BitnumIntr(s1, 2, 7) | BitnumIntr(s0, 2, 6) | BitnumIntr(s1, 10, 5) | BitnumIntr(s0, 10, 4) | BitnumIntr(s1, 18, 3) | BitnumIntr(s0, 18, 2) | BitnumIntr(s1, 26, 1) | BitnumIntr(s0, 26, 0));
        output[offset + 5] = (byte)(BitnumIntr(s1, 1, 7) | BitnumIntr(s0, 1, 6) | BitnumIntr(s1, 9, 5) | BitnumIntr(s0, 9, 4) | BitnumIntr(s1, 17, 3) | BitnumIntr(s0, 17, 2) | BitnumIntr(s1, 25, 1) | BitnumIntr(s0, 25, 0));
        output[offset + 4] = (byte)(BitnumIntr(s1, 0, 7) | BitnumIntr(s0, 0, 6) | BitnumIntr(s1, 8, 5) | BitnumIntr(s0, 8, 4) | BitnumIntr(s1, 16, 3) | BitnumIntr(s0, 16, 2) | BitnumIntr(s1, 24, 1) | BitnumIntr(s0, 24, 0));
    }

    private static int DesF(int state, int[] key)
    {
        int t1 =
            BitnumIntl(state, 31, 0) | (int)(((uint)(state & unchecked((int)0xF0000000))) >>> 1) | BitnumIntl(state, 4, 5) |
            BitnumIntl(state, 3, 6) | (int)(((uint)(state & 0x0F000000)) >>> 3) | BitnumIntl(state, 8, 11) |
            BitnumIntl(state, 7, 12) | (int)(((uint)(state & 0x00F00000)) >>> 5) | BitnumIntl(state, 12, 17) |
            BitnumIntl(state, 11, 18) | (int)(((uint)(state & 0x000F0000)) >>> 7) | BitnumIntl(state, 16, 23);

        int t2 =
            BitnumIntl(state, 15, 0) | ((state & 0x0000F000) << 15) | BitnumIntl(state, 20, 5) |
            BitnumIntl(state, 19, 6) | ((state & 0x00000F00) << 13) | BitnumIntl(state, 24, 11) |
            BitnumIntl(state, 23, 12) | ((state & 0x000000F0) << 11) | BitnumIntl(state, 28, 17) |
            BitnumIntl(state, 27, 18) | ((state & 0x0000000F) << 9) | BitnumIntl(state, 0, 23);

        Span<int> lrgstate = stackalloc int[6]
        {
            (t1 >>> 24) & 0xFF,
            (t1 >>> 16) & 0xFF,
            (t1 >>> 8) & 0xFF,
            (t2 >>> 24) & 0xFF,
            (t2 >>> 16) & 0xFF,
            (t2 >>> 8) & 0xFF
        };

        for (int i = 0; i < 6; i++)
        {
            lrgstate[i] ^= key[i];
        }

        int s =
            (Sbox[0][SboxBit(lrgstate[0] >>> 2)] << 28) |
            (Sbox[1][SboxBit(((lrgstate[0] & 0x03) << 4) | (lrgstate[1] >>> 4))] << 24) |
            (Sbox[2][SboxBit(((lrgstate[1] & 0x0F) << 2) | (lrgstate[2] >>> 6))] << 20) |
            (Sbox[3][SboxBit(lrgstate[2] & 0x3F)] << 16) |
            (Sbox[4][SboxBit(lrgstate[3] >>> 2)] << 12) |
            (Sbox[5][SboxBit(((lrgstate[3] & 0x03) << 4) | (lrgstate[4] >>> 4))] << 8) |
            (Sbox[6][SboxBit(((lrgstate[4] & 0x0F) << 2) | (lrgstate[5] >>> 6))] << 4) |
            Sbox[7][SboxBit(lrgstate[5] & 0x3F)];

        return BitnumIntl(s, 15, 0) | BitnumIntl(s, 6, 1) | BitnumIntl(s, 19, 2) | BitnumIntl(s, 20, 3) |
            BitnumIntl(s, 28, 4) | BitnumIntl(s, 11, 5) | BitnumIntl(s, 27, 6) | BitnumIntl(s, 16, 7) |
            BitnumIntl(s, 0, 8) | BitnumIntl(s, 14, 9) | BitnumIntl(s, 22, 10) | BitnumIntl(s, 25, 11) |
            BitnumIntl(s, 4, 12) | BitnumIntl(s, 17, 13) | BitnumIntl(s, 30, 14) | BitnumIntl(s, 9, 15) |
            BitnumIntl(s, 1, 16) | BitnumIntl(s, 7, 17) | BitnumIntl(s, 23, 18) | BitnumIntl(s, 13, 19) |
            BitnumIntl(s, 31, 20) | BitnumIntl(s, 26, 21) | BitnumIntl(s, 2, 22) | BitnumIntl(s, 8, 23) |
            BitnumIntl(s, 18, 24) | BitnumIntl(s, 12, 25) | BitnumIntl(s, 29, 26) | BitnumIntl(s, 5, 27) |
            BitnumIntl(s, 21, 28) | BitnumIntl(s, 10, 29) | BitnumIntl(s, 3, 30) | BitnumIntl(s, 24, 31);
    }

    private static void DesCrypt(ReadOnlySpan<byte> input, int inOffset, int[][] schedule, Span<byte> output, int outOffset)
    {
        var (s0, s1) = InitialPermutation(input, inOffset);
        for (int i = 0; i < 15; i++)
        {
            int prev = s1;
            s1 = DesF(s1, schedule[i]) ^ s0;
            s0 = prev;
        }
        s0 = DesF(s1, schedule[15]) ^ s0;
        InversePermutation(s0, s1, output, outOffset);
    }

    private static int[][] KeySchedule(ReadOnlySpan<byte> key, int keyOffset, int mode)
    {
        var keySlice = key.Slice(keyOffset, 8);
        var schedule = new int[16][];
        for (int i = 0; i < 16; i++)
        {
            schedule[i] = new int[6];
        }

        int c = 0;
        int d = 0;
        for (int i = 0; i < 28; i++)
        {
            c |= Bitnum(keySlice, KeyPermC[i], 31 - i);
            d |= Bitnum(keySlice, KeyPermD[i], 31 - i);
        }

        for (int i = 0; i < 16; i++)
        {
            c = (int)((((uint)c << KeyRndShift[i]) | ((uint)c >>> (28 - KeyRndShift[i]))) & 0xFFFFFFF0u);
            d = (int)((((uint)d << KeyRndShift[i]) | ((uint)d >>> (28 - KeyRndShift[i]))) & 0xFFFFFFF0u);
            int togen = mode == DesDecrypt ? 15 - i : i;
            for (int j = 0; j < 24; j++)
            {
                schedule[togen][j / 8] |= BitnumIntr(c, KeyCompression[j], 7 - (j % 8));
            }
            for (int j = 24; j < 48; j++)
            {
                schedule[togen][j / 8] |= BitnumIntr(d, KeyCompression[j] - 27, 7 - (j % 8));
            }
        }
        return schedule;
    }

    private static int[][][] TripleDesKeySetup(ReadOnlySpan<byte> key, int mode)
    {
        if (mode == DesEncrypt)
        {
            return
            [
                KeySchedule(key, 0, DesEncrypt),
                KeySchedule(key, 8, DesDecrypt),
                KeySchedule(key, 16, DesEncrypt)
            ];
        }
        return
        [
            KeySchedule(key, 16, DesDecrypt),
            KeySchedule(key, 8, DesEncrypt),
            KeySchedule(key, 0, DesDecrypt)
        ];
    }

    private static void TripleDesCrypt(ReadOnlySpan<byte> input, int inOffset, int[][][] schedule, Span<byte> output, int outOffset)
    {
        Span<byte> buf1 = stackalloc byte[8];
        Span<byte> buf2 = stackalloc byte[8];
        DesCrypt(input, inOffset, schedule[0], buf1, 0);
        DesCrypt(buf1, 0, schedule[1], buf2, 0);
        DesCrypt(buf2, 0, schedule[2], output, outOffset);
    }

    public static string DecodeQrc(string? hexData)
    {
        if (string.IsNullOrWhiteSpace(hexData)) return "";
        var hex = hexData.Trim();
        if (hex.Length % 2 != 0) return "";
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromHexString(hex);
        }
        catch
        {
            return "";
        }
        if (encrypted.Length == 0 || (encrypted.Length % 8 != 0)) return "";

        byte[] decrypted = new byte[encrypted.Length];
        int blockCount = encrypted.Length / 8;
        for (int i = 0; i < blockCount; i++)
        {
            TripleDesCrypt(encrypted, i * 8, CachedDecryptSchedule, decrypted, i * 8);
        }

        try
        {
            using var ms = new MemoryStream(decrypted);
            using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            zlib.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }
        catch
        {
            try
            {
                int offset = (decrypted.Length > 2 && decrypted[0] == 0x78) ? 2 : 0;
                using var ms = new MemoryStream(decrypted, offset, decrypted.Length - offset);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                deflate.CopyTo(outMs);
                return Encoding.UTF8.GetString(outMs.ToArray());
            }
            catch
            {
                return "";
            }
        }
    }
}
