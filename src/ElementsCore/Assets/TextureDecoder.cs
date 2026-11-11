using System.Buffers.Binary;

namespace ElementsCore.Assets;

/// <summary>A decoded picture: BGRA, 4 bytes per pixel, rows top to bottom.</summary>
public sealed record Bgra(int Width, int Height, byte[] Pixels);

/// <summary>
/// Minimal DDS / TGA decoder for game icons: DXT1, DXT3, DXT5, 32/24/16-bit uncompressed DDS,
/// uncompressed / RLE TGA. <see cref="DecodeDds"/> can decode just a rectangle (one icon of an atlas)
/// so an 8192×4096 icon sheet is never expanded in memory.
/// </summary>
public static class TextureDecoder
{
    public readonly record struct DdsInfo(int Width, int Height, string Format, int DataOffset, int Bpp, uint RMask, uint GMask, uint BMask, uint AMask);

    public static bool IsDds(byte[] d) => d.Length > 128 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' ';

    public static DdsInfo ReadDdsInfo(byte[] d)
    {
        if (!IsDds(d)) throw new InvalidDataException(L.T("Not a DDS file."));
        int h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        int w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80));
        string four = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int bpp = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(88));
        uint rm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(92)), gm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(96));
        uint bm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(100)), am = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(104));
        bool fourcc = (pfFlags & 0x4) != 0;
        if ((pfFlags & 0x1) == 0) am = 0; // no alpha channel declared
        return new DdsInfo(w, h, fourcc ? four : $"RGB{bpp}", 128, bpp, rm, gm, bm, am);
    }

    /// <summary>Decodes the whole DDS, or the rectangle (x, y, w, h) of it.</summary>
    public static Bgra DecodeDds(byte[] d, Rectangle? region = null)
    {
        var info = ReadDdsInfo(d);
        var r = region ?? new Rectangle(0, 0, info.Width, info.Height);
        r = Rectangle.Intersect(r, new Rectangle(0, 0, info.Width, info.Height));
        var outp = new byte[Math.Max(0, r.W) * Math.Max(0, r.H) * 4];
        if (r.W <= 0 || r.H <= 0) return new Bgra(0, 0, outp);
        switch (info.Format)
        {
            case "DXT1": DecodeBlocks(d, info, r, outp, 8, Dxt1Block); break;
            case "DXT2":
            case "DXT3": DecodeBlocks(d, info, r, outp, 16, Dxt3Block); break;
            case "DXT4":
            case "DXT5": DecodeBlocks(d, info, r, outp, 16, Dxt5Block); break;
            default:
                if (info.Format.StartsWith("RGB")) DecodeRgb(d, info, r, outp);
                else throw new NotSupportedException(L.F("DDS format {0} is not supported yet.", info.Format));
                break;
        }
        return new Bgra(r.W, r.H, outp);
    }

    public readonly record struct Rectangle(int X, int Y, int W, int H)
    {
        public static Rectangle Intersect(Rectangle a, Rectangle b)
        {
            int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
            int x1 = Math.Min(a.X + a.W, b.X + b.W), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }
    }

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<uint> px16);

    private static void DecodeBlocks(byte[] d, DdsInfo info, Rectangle r, byte[] outp, int blockBytes, BlockDecoder dec)
    {
        int bw = Math.Max(1, (info.Width + 3) / 4);
        Span<uint> px = stackalloc uint[16];
        for (int by = r.Y / 4; by <= (r.Y + r.H - 1) / 4; by++)
            for (int bx = r.X / 4; bx <= (r.X + r.W - 1) / 4; bx++)
            {
                long at = info.DataOffset + ((long)by * bw + bx) * blockBytes;
                if (at + blockBytes > d.Length) continue;
                dec(d.AsSpan((int)at, blockBytes), px);
                for (int j = 0; j < 4; j++)
                {
                    int y = by * 4 + j - r.Y;
                    if (y < 0 || y >= r.H) continue;
                    for (int i = 0; i < 4; i++)
                    {
                        int x = bx * 4 + i - r.X;
                        if (x < 0 || x >= r.W) continue;
                        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan((y * r.W + x) * 4), px[j * 4 + i]);
                    }
                }
            }
    }

    private static uint Bgra565(ushort c, byte a = 255)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        return (uint)((a << 24) | (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2)));
    }

    private static void ColorBlock(ReadOnlySpan<byte> b, Span<uint> px, bool dxt1)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        Span<uint> pal = stackalloc uint[4];
        pal[0] = Bgra565(c0); pal[1] = Bgra565(c1);
        uint Mix(uint x, uint y, int wx, int wy, int div)
        {
            uint ch(int sh) => (uint)((((x >> sh) & 255) * wx + ((y >> sh) & 255) * wy) / div);
            return 0xFF000000u | (ch(16) << 16) | (ch(8) << 8) | ch(0);
        }
        if (c0 > c1 || !dxt1) { pal[2] = Mix(pal[0], pal[1], 2, 1, 3); pal[3] = Mix(pal[0], pal[1], 1, 2, 3); }
        else { pal[2] = Mix(pal[0], pal[1], 1, 1, 2); pal[3] = 0; }
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (int i = 0; i < 16; i++) px[i] = pal[(int)((idx >> (i * 2)) & 3)];
    }

    private static void Dxt1Block(ReadOnlySpan<byte> b, Span<uint> px) => ColorBlock(b, px, true);

    private static void Dxt3Block(ReadOnlySpan<byte> b, Span<uint> px)
    {
        ColorBlock(b[8..], px, false);
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(b);
        for (int i = 0; i < 16; i++)
        {
            uint v = (uint)((a >> (i * 4)) & 15);
            px[i] = (px[i] & 0x00FFFFFF) | ((v * 17) << 24);
        }
    }

    private static void Dxt5Block(ReadOnlySpan<byte> b, Span<uint> px)
    {
        ColorBlock(b[8..], px, false);
        int a0 = b[0], a1 = b[1];
        Span<int> al = stackalloc int[8];
        al[0] = a0; al[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) al[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i < 5; i++) al[i + 1] = ((5 - i) * a0 + i * a1) / 5; al[6] = 0; al[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            px[i] = (px[i] & 0x00FFFFFF) | ((uint)al[(int)((bits >> (3 * i)) & 7)] << 24);
    }

    private static void DecodeRgb(byte[] d, DdsInfo info, Rectangle r, byte[] outp)
    {
        int bytes = Math.Max(1, info.Bpp / 8);
        long pitch = (long)info.Width * bytes;
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                long at = info.DataOffset + (r.Y + y) * pitch + (long)(r.X + x) * bytes;
                if (at + bytes > d.Length) continue;
                uint v = bytes switch
                {
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at)),
                    3 => (uint)(d[at] | (d[at + 1] << 8) | (d[at + 2] << 16)),
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)at)),
                    _ => d[at],
                };
                byte Ch(uint mask) => mask == 0 ? (byte)0 : (byte)(((v & mask) >> System.Numerics.BitOperations.TrailingZeroCount(mask)) * 255 / (mask >> System.Numerics.BitOperations.TrailingZeroCount(mask)));
                byte R = Ch(info.RMask), G = Ch(info.GMask), B = Ch(info.BMask);
                byte A = info.AMask == 0 ? (byte)255 : Ch(info.AMask);
                if (info.RMask == 0 && info.GMask == 0 && info.BMask == 0) R = G = B = (byte)v; // luminance
                int o = (y * r.W + x) * 4;
                outp[o] = B; outp[o + 1] = G; outp[o + 2] = R; outp[o + 3] = A;
            }
    }

    /// <summary>Uncompressed (type 2/3) or RLE (type 10/11) TGA, 8/16/24/32 bit.</summary>
    public static Bgra DecodeTga(byte[] d)
    {
        int idLen = d[0], cmapType = d[1], type = d[2];
        int cmapLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(5)), cmapBits = d[7];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12)), h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14));
        int bpp = d[16], desc = d[17];
        bool topDown = (desc & 0x20) != 0;
        int p = 18 + idLen + (cmapType == 1 ? cmapLen * ((cmapBits + 7) / 8) : 0);
        int bytes = bpp / 8;
        var outp = new byte[w * h * 4];
        bool rle = type is 9 or 10 or 11;
        int total = w * h, n = 0;
        void Put(int i, ReadOnlySpan<byte> s)
        {
            int x = i % w, y = i / w;
            if (!topDown) y = h - 1 - y;
            int o = (y * w + x) * 4;
            switch (bytes)
            {
                case 4: outp[o] = s[0]; outp[o + 1] = s[1]; outp[o + 2] = s[2]; outp[o + 3] = s[3]; break;
                case 3: outp[o] = s[0]; outp[o + 1] = s[1]; outp[o + 2] = s[2]; outp[o + 3] = 255; break;
                case 2:
                    {
                        int v = s[0] | (s[1] << 8);
                        outp[o] = (byte)((v & 31) << 3); outp[o + 1] = (byte)(((v >> 5) & 31) << 3); outp[o + 2] = (byte)(((v >> 10) & 31) << 3);
                        outp[o + 3] = (byte)((v & 0x8000) != 0 || bpp == 15 ? 255 : 255);
                        break;
                    }
                default: outp[o] = outp[o + 1] = outp[o + 2] = s[0]; outp[o + 3] = 255; break;
            }
        }
        while (n < total && p < d.Length)
        {
            if (!rle) { Put(n++, d.AsSpan(p, bytes)); p += bytes; continue; }
            int hdr = d[p++];
            int count = (hdr & 0x7F) + 1;
            if ((hdr & 0x80) != 0) { var px = d.AsSpan(p, bytes); p += bytes; for (int k = 0; k < count && n < total; k++) Put(n++, px); }
            else for (int k = 0; k < count && n < total; k++) { Put(n++, d.AsSpan(p, bytes)); p += bytes; }
        }
        return new Bgra(w, h, outp);
    }

    /// <summary>DDS / TGA (by content), whole picture.</summary>
    public static Bgra? Decode(byte[] d)
    {
        try
        {
            if (IsDds(d)) return DecodeDds(d);
            if (d.Length > 18 && d[2] is 1 or 2 or 3 or 9 or 10 or 11) return DecodeTga(d);
        }
        catch { }
        return null;
    }
}
