using System.IO.Compression;
using System.Text;

namespace IH.Shared;

/// <summary>Anything that can travel over the network.</summary>
public interface IWire
{
    /// <summary>One method for both directions, so reading and writing can never drift apart.</summary>
    void Sync(Wire w);
}

/// <summary>
/// Compact bidirectional binary serializer (varints + zigzag). A single Sync method per DTO
/// both writes and reads, depending on <see cref="Writing"/>.
/// </summary>
public sealed class Wire
{
    public readonly bool Writing;
    private readonly MemoryStream _ms;
    private readonly BinaryWriter? _w;
    private readonly BinaryReader? _r;

    public Wire()
    {
        Writing = true;
        _ms = new MemoryStream(256);
        _w = new BinaryWriter(_ms, Encoding.UTF8);
    }

    public Wire(byte[] data, int offset, int count)
    {
        Writing = false;
        _ms = new MemoryStream(data, offset, count, false);
        _r = new BinaryReader(_ms, Encoding.UTF8);
    }

    public byte[] ToArray() { _w!.Flush(); return _ms.ToArray(); }

    public void I(ref int v)
    {
        if (Writing) _w!.Write7BitEncodedInt((v << 1) ^ (v >> 31));
        else { int z = _r!.Read7BitEncodedInt(); v = (int)((uint)z >> 1) ^ -(z & 1); }
    }

    public void L(ref long v)
    {
        if (Writing) _w!.Write7BitEncodedInt64((v << 1) ^ (v >> 63));
        else { long z = _r!.Read7BitEncodedInt64(); v = (long)((ulong)z >> 1) ^ -(z & 1); }
    }

    public void U(ref ulong v) { if (Writing) _w!.Write(v); else v = _r!.ReadUInt64(); }
    public void By(ref byte v) { if (Writing) _w!.Write(v); else v = _r!.ReadByte(); }
    public void B(ref bool v) { if (Writing) _w!.Write(v); else v = _r!.ReadBoolean(); }
    public void F(ref float v) { if (Writing) _w!.Write(v); else v = _r!.ReadSingle(); }
    public void S(ref string v) { if (Writing) _w!.Write(v ?? ""); else v = _r!.ReadString(); }

    private int Len(int n)
    {
        I(ref n);
        if (!Writing && (n < 0 || n > 4_000_000)) throw new InvalidDataException("bad array length");
        return n;
    }

    public void IA(ref int[] a)
    {
        int n = Len(a?.Length ?? 0);
        if (!Writing) a = new int[n];
        for (int i = 0; i < n; i++) I(ref a![i]);
    }

    public void SA(ref string[] a)
    {
        int n = Len(a?.Length ?? 0);
        if (!Writing) a = new string[n];
        for (int i = 0; i < n; i++) S(ref a![i]);
    }

    public void A<T>(ref T[] a) where T : class, IWire, new()
    {
        int n = Len(a?.Length ?? 0);
        if (!Writing) a = new T[n];
        for (int i = 0; i < n; i++)
        {
            if (!Writing) a![i] = new T();
            a![i].Sync(this);
        }
    }

    /// <summary>Nullable nested object.</summary>
    public void O<T>(ref T? o) where T : class, IWire, new()
    {
        bool has = o != null;
        B(ref has);
        if (!has) { o = null; return; }
        if (!Writing) o = new T();
        o!.Sync(this);
    }

    public static byte[] ToBytes(IWire o)
    {
        var w = new Wire();
        o.Sync(w);
        return w.ToArray();
    }
}

/// <summary>Message framing: [type:1][flags:1][payload]. Large payloads are deflate-compressed.</summary>
public static class Codec
{
    public const int ProtocolVersion = 6;
    public const string ConnectionKey = "InfiniteHorizons/1";
    private const int CompressThreshold = 400;

    public static byte[] Encode(MsgType type, IWire? body)
    {
        byte[] payload = body == null ? Array.Empty<byte>() : Wire.ToBytes(body);
        byte flags = 0;
        if (payload.Length > CompressThreshold)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(payload, 0, payload.Length);
            if (ms.Length < payload.Length) { payload = ms.ToArray(); flags = 1; }
        }
        var res = new byte[payload.Length + 2];
        res[0] = (byte)type;
        res[1] = flags;
        Buffer.BlockCopy(payload, 0, res, 2, payload.Length);
        return res;
    }

    public static MsgType PeekType(byte[] data, int offset) => (MsgType)data[offset];

    public static T Decode<T>(byte[] data, int offset, int count) where T : class, IWire, new()
    {
        if (count < 2) throw new InvalidDataException("short packet");
        byte[] payload = data; int off = offset + 2, len = count - 2;
        if ((data[offset + 1] & 1) != 0)
        {
            using var src = new MemoryStream(data, off, len, false);
            using var z = new DeflateStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            z.CopyTo(dst);
            if (dst.Length > 32_000_000) throw new InvalidDataException("packet too large");
            payload = dst.ToArray(); off = 0; len = payload.Length;
        }
        var t = new T();
        t.Sync(new Wire(payload, off, len));
        return t;
    }
}
