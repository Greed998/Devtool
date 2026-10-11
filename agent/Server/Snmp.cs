using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DevToolbox.Fleet.Server;

// =====================================================================================================================
//  SNMP-trap fogadás (1.5.5, a felhasználó kérése) – saját, függőség nélküli megvalósítás:
//  v1 trap · v2c trap + inform (válasszal) · v3 trap + inform (USM: felderítés, időszinkron-jelentés, válasz).
//  Hitelesítés: HMAC-MD5-96, HMAC-SHA-96 (RFC 3414), HMAC-SHA-2 (RFC 7860: 224/256/384/512).
//  Titkosítás: DES-CBC (RFC 3414), AES-128-CFB (RFC 3826), AES-192/256 (Blumenthal-féle kulcsbővítés, mint a Net-SNMP),
//  AES-192/256 „C” és 3DES (Reeder/Cisco-féle kulcsbővítés).
//  A bejövő csomag megbízhatatlan adat: szigorú, hosszkorlátos BER-olvasó, minden hiba elutasítás (nincs kivétel kifelé).
// =====================================================================================================================

public sealed class SnmpFormatException : Exception { public SnmpFormatException(string m) : base(m) { } }

/// <summary>Szigorú BER-olvasó (csak a határozott hosszú formát fogadja, legfeljebb 3 hosszbájttal).</summary>
public sealed class BerReader
{
    public readonly record struct Tlv(byte Tag, int TlvStart, int Start, int Len) { public int End => Start + Len; }

    private readonly byte[] _b;
    private int _p;
    private readonly int _end;

    public BerReader(byte[] b, int start, int end) { _b = b; _p = start; _end = end; }
    public BerReader(byte[] b) : this(b, 0, b.Length) { }

    public bool More => _p < _end;
    public byte[] Buffer => _b;

    public Tlv Next()
    {
        if (_p >= _end) throw new SnmpFormatException("váratlan vég");
        var s = _p;
        var tag = _b[_p++];
        if ((tag & 0x1F) == 0x1F) throw new SnmpFormatException("többbájtos tag");
        if (_p >= _end) throw new SnmpFormatException("hiányzó hossz");
        int len = _b[_p++];
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            if (n is 0 or > 3) throw new SnmpFormatException("érvénytelen hosszmező");
            len = 0;
            for (int i = 0; i < n; i++)
            {
                if (_p >= _end) throw new SnmpFormatException("csonka hossz");
                len = (len << 8) | _b[_p++];
            }
        }
        if (len > _end - _p) throw new SnmpFormatException("az elem hosszabb a csomagnál");
        var t = new Tlv(tag, s, _p, len);
        _p += len;
        return t;
    }

    public Tlv Expect(byte tag, string what)
    {
        var t = Next();
        if (t.Tag != tag) throw new SnmpFormatException($"{what}: 0x{tag:X2} helyett 0x{t.Tag:X2}");
        return t;
    }

    public BerReader Enter(Tlv t) => new(_b, t.Start, t.End);
    public BerReader Seq(string what) => Enter(Expect(0x30, what));
    public long Int(string what) => Ber.Signed(_b, Expect(0x02, what));
    public byte[] Octets(string what) { var t = Expect(0x04, what); return _b.AsSpan(t.Start, t.Len).ToArray(); }
}

/// <summary>BER-kódolás (írás) és az értékek olvasása.</summary>
public static class Ber
{
    public static long Signed(byte[] b, BerReader.Tlv t)
    {
        if (t.Len is 0 or > 8) throw new SnmpFormatException("érvénytelen egész");
        long v = (sbyte)b[t.Start];
        for (int i = 1; i < t.Len; i++) v = (v << 8) | b[t.Start + i];
        return v;
    }

    public static ulong Unsigned(byte[] b, BerReader.Tlv t)
    {
        if (t.Len is 0 or > 9 || (t.Len == 9 && b[t.Start] != 0)) throw new SnmpFormatException("érvénytelen előjel nélküli egész");
        ulong v = 0;
        for (int i = 0; i < t.Len; i++) v = (v << 8) | b[t.Start + i];
        return v;
    }

    public static string Oid(byte[] b, BerReader.Tlv t)
    {
        if (t.Len is 0 or > 512) throw new SnmpFormatException("érvénytelen OID");
        var parts = new List<ulong>();
        ulong cur = 0; int bytes = 0;
        for (int i = 0; i < t.Len; i++)
        {
            var c = b[t.Start + i];
            cur = (cur << 7) | (uint)(c & 0x7F);
            if (++bytes > 5 || cur > uint.MaxValue + 80UL) throw new SnmpFormatException("túl nagy OID-tag");
            if ((c & 0x80) == 0) { parts.Add(cur); cur = 0; bytes = 0; }
        }
        if (bytes != 0 || parts.Count == 0 || parts.Count > 128) throw new SnmpFormatException("csonka OID");
        var first = parts[0];
        var x = first < 40 ? 0UL : first < 80 ? 1UL : 2UL;
        var sb = new StringBuilder().Append(x).Append('.').Append(first - 40 * x);
        for (int i = 1; i < parts.Count; i++) sb.Append('.').Append(parts[i]);
        return sb.ToString();
    }

    public static int HeaderLen(int contentLen) => 1 + (contentLen < 0x80 ? 1 : contentLen <= 0xFF ? 2 : contentLen <= 0xFFFF ? 3 : 4);

    public static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
    {
        var n = content.Length;
        var head = n < 0x80 ? new[] { tag, (byte)n }
                 : n <= 0xFF ? new[] { tag, (byte)0x81, (byte)n }
                 : n <= 0xFFFF ? new[] { tag, (byte)0x82, (byte)(n >> 8), (byte)n }
                 : new[] { tag, (byte)0x83, (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        var r = new byte[head.Length + n];
        head.CopyTo(r, 0);
        content.CopyTo(r.AsSpan(head.Length));
        return r;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    public static byte[] Seq(params byte[][] items) => Tlv(0x30, Concat(items));
    public static byte[] Oct(ReadOnlySpan<byte> v) => Tlv(0x04, v);
    public static byte[] Null() => new byte[] { 0x05, 0x00 };

    public static byte[] Int(long v)
    {
        var bytes = new List<byte>();
        var x = v;
        do { bytes.Insert(0, (byte)x); x >>= 8; } while (!(x == 0 && (bytes[0] & 0x80) == 0) && !(x == -1 && (bytes[0] & 0x80) != 0));
        return Tlv(0x02, bytes.ToArray());
    }

    /// <summary>Előjel nélküli egész a megadott taggel (Counter32 0x41, Gauge32 0x42, TimeTicks 0x43).</summary>
    public static byte[] UInt(byte tag, ulong v)
    {
        var bytes = new List<byte>();
        do { bytes.Insert(0, (byte)v); v >>= 8; } while (v != 0);
        if ((bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return Tlv(tag, bytes.ToArray());
    }

    public static byte[] Oid(string oid)
    {
        var p = oid.Split('.').Select(uint.Parse).ToArray();
        if (p.Length < 2) throw new ArgumentException("OID");
        var outp = new List<byte>();
        void Sub(ulong v)
        {
            var tmp = new Stack<byte>();
            tmp.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) tmp.Push((byte)(0x80 | (v & 0x7F)));
            outp.AddRange(tmp);
        }
        Sub(p[0] * 40UL + p[1]);
        for (int i = 2; i < p.Length; i++) Sub(p[i]);
        return Tlv(0x06, outp.ToArray());
    }

    /// <summary>Egy érték típusa és olvasható szövege (a felületnek; legfeljebb 500 karakter).</summary>
    public static (string Type, string Text) Value(byte[] b, BerReader.Tlv t)
    {
        string Cut(string s) => s.Length <= 500 ? s : s[..500] + "…";
        switch (t.Tag)
        {
            case 0x02: return ("Integer", Signed(b, t).ToString());
            case 0x04:
            {
                var v = b.AsSpan(t.Start, t.Len);
                if (TryText(v, out var s)) return ("OctetString", Cut(s));
                if (v.Length == 6) return ("OctetString", string.Join(":", v.ToArray().Select(x => x.ToString("x2"))));
                return ("OctetString", Cut("0x" + Convert.ToHexString(v)));
            }
            case 0x05: return ("Null", "");
            case 0x06: return ("OID", Oid(b, t));
            case 0x40: return ("IpAddress", t.Len == 4 ? new IPAddress(b.AsSpan(t.Start, 4)).ToString() : "?");
            case 0x41: return ("Counter32", Unsigned(b, t).ToString());
            case 0x42: return ("Gauge32", Unsigned(b, t).ToString());
            case 0x43: return ("TimeTicks", Ticks(Unsigned(b, t)));
            case 0x44: return ("Opaque", Cut("0x" + Convert.ToHexString(b.AsSpan(t.Start, t.Len))));
            case 0x46: return ("Counter64", Unsigned(b, t).ToString());
            case 0x47: return ("UInteger32", Unsigned(b, t).ToString());
            case 0x80: return ("noSuchObject", "");
            case 0x81: return ("noSuchInstance", "");
            case 0x82: return ("endOfMibView", "");
            default: return ($"0x{t.Tag:X2}", Cut("0x" + Convert.ToHexString(b.AsSpan(t.Start, Math.Min(t.Len, 200)))));
        }
    }

    /// <summary>Időtartam századmásodpercből: „3 nap 04:05:06”.</summary>
    public static string Ticks(ulong cs)
    {
        var ts = TimeSpan.FromMilliseconds(cs * 10.0);
        return (ts.Days > 0 ? $"{ts.Days} nap " : "") + $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private static bool TryText(ReadOnlySpan<byte> v, out string s)
    {
        s = "";
        if (v.Length == 0) return true;
        try { s = new UTF8Encoding(false, true).GetString(v); }
        catch { return false; }
        foreach (var c in s.TrimEnd('\0')) if (char.IsControl(c) && c is not '\r' and not '\n' and not '\t') return false;
        s = s.TrimEnd('\0');
        return true;
    }
}

public sealed record SnmpVarBind(string Oid, string Type, string Value);

/// <summary>Egy PDU (v2-forma; a v1 trapnél az Enterprise / Generic / Specific is).</summary>
public sealed class SnmpPdu
{
    public byte Type { get; init; }
    public long RequestId { get; set; }
    public List<SnmpVarBind> VarBinds { get; } = new();
    public byte[] RawVarBinds { get; set; } = Array.Empty<byte>();
    public string Enterprise { get; set; } = "";
    public string AgentAddr { get; set; } = "";
    public long Generic { get; set; }
    public long Specific { get; set; }
    public ulong TimeStamp { get; set; }

    public const byte GetRequest = 0xA0, Response = 0xA2, TrapV1 = 0xA4, Inform = 0xA6, TrapV2 = 0xA7, Report = 0xA8;

    public static SnmpPdu Parse(BerReader r)
    {
        var b = r.Buffer;
        var t = r.Next();
        if (t.Tag is < 0xA0 or > 0xA8) throw new SnmpFormatException("ismeretlen PDU");
        var p = new SnmpPdu { Type = t.Tag };
        var pr = r.Enter(t);
        if (t.Tag == TrapV1)
        {
            p.Enterprise = Ber.Oid(b, pr.Expect(0x06, "enterprise"));
            var a = pr.Expect(0x40, "agent-addr");
            p.AgentAddr = a.Len == 4 ? new IPAddress(b.AsSpan(a.Start, 4)).ToString() : "";
            p.Generic = pr.Int("generic-trap");
            p.Specific = pr.Int("specific-trap");
            p.TimeStamp = Ber.Unsigned(b, pr.Expect(0x43, "time-stamp"));
        }
        else
        {
            p.RequestId = pr.Int("request-id");
            pr.Int("error-status");
            pr.Int("error-index");
        }
        var vt = pr.Expect(0x30, "varbind-lista");
        p.RawVarBinds = b.AsSpan(vt.TlvStart, vt.End - vt.TlvStart).ToArray();
        var vr = pr.Enter(vt);
        while (vr.More && p.VarBinds.Count < 200)
        {
            var vb = vr.Seq("varbind");
            var oid = Ber.Oid(b, vb.Expect(0x06, "varbind-OID"));
            var (type, text) = Ber.Value(b, vb.Next());
            p.VarBinds.Add(new SnmpVarBind(oid, type, text));
        }
        return p;
    }
}

public enum SnmpAuthProto { None, Md5, Sha1, Sha224, Sha256, Sha384, Sha512 }
public enum SnmpPrivProto { None, Des, TripleDes, Aes128, Aes192, Aes256, Aes192C, Aes256C }

/// <summary>Az USM kriptográfiája: kulcs a jelszóból, lokalizálás az engineID-vel, HMAC, titkosítás.</summary>
public static class Usm
{
    public static SnmpAuthProto Auth(string s) => s.ToLowerInvariant() switch
    {
        "md5" => SnmpAuthProto.Md5, "sha" or "sha1" => SnmpAuthProto.Sha1, "sha224" => SnmpAuthProto.Sha224, "sha256" => SnmpAuthProto.Sha256,
        "sha384" => SnmpAuthProto.Sha384, "sha512" => SnmpAuthProto.Sha512, _ => SnmpAuthProto.None
    };

    public static SnmpPrivProto Priv(string s) => s.ToLowerInvariant() switch
    {
        "des" => SnmpPrivProto.Des, "3des" => SnmpPrivProto.TripleDes, "aes" or "aes128" => SnmpPrivProto.Aes128,
        "aes192" => SnmpPrivProto.Aes192, "aes256" => SnmpPrivProto.Aes256, "aes192c" => SnmpPrivProto.Aes192C, "aes256c" => SnmpPrivProto.Aes256C,
        _ => SnmpPrivProto.None
    };

    public static readonly string[] AuthNames = { "none", "md5", "sha1", "sha224", "sha256", "sha384", "sha512" };
    public static readonly string[] PrivNames = { "none", "des", "3des", "aes128", "aes192", "aes256", "aes192c", "aes256c" };

    /// <summary>A hitelesítő kód hossza az üzenetben (RFC 3414 / 7860).</summary>
    public static int MacLen(SnmpAuthProto a) => a switch
    {
        SnmpAuthProto.Md5 or SnmpAuthProto.Sha1 => 12, SnmpAuthProto.Sha224 => 16, SnmpAuthProto.Sha256 => 24,
        SnmpAuthProto.Sha384 => 32, SnmpAuthProto.Sha512 => 48, _ => 0
    };

    public static byte[] Hash(SnmpAuthProto a, ReadOnlySpan<byte> d) => a switch
    {
        SnmpAuthProto.Md5 => MD5.HashData(d), SnmpAuthProto.Sha1 => SHA1.HashData(d), SnmpAuthProto.Sha224 => Sha224.Hash(d),
        SnmpAuthProto.Sha256 => SHA256.HashData(d), SnmpAuthProto.Sha384 => SHA384.HashData(d), SnmpAuthProto.Sha512 => SHA512.HashData(d),
        _ => throw new ArgumentException("nincs hash")
    };

    public static byte[] Hmac(SnmpAuthProto a, byte[] key, ReadOnlySpan<byte> d) => a switch
    {
        SnmpAuthProto.Md5 => HMACMD5.HashData(key, d), SnmpAuthProto.Sha1 => HMACSHA1.HashData(key, d), SnmpAuthProto.Sha224 => Sha224.Hmac(key, d),
        SnmpAuthProto.Sha256 => HMACSHA256.HashData(key, d), SnmpAuthProto.Sha384 => HMACSHA384.HashData(key, d), SnmpAuthProto.Sha512 => HMACSHA512.HashData(key, d),
        _ => throw new ArgumentException("nincs hash")
    };

    /// <summary>RFC 3414 A.2: a jelszó 1 MB-nyi ismétlésének hash-e (Ku).</summary>
    public static byte[] PasswordToKey(SnmpAuthProto a, ReadOnlySpan<byte> password)
    {
        if (password.Length == 0) throw new ArgumentException("üres jelszó");
        var buf = new byte[1048576];
        for (int i = 0; i < buf.Length; i++) buf[i] = password[i % password.Length];
        try { return Hash(a, buf); }
        finally { CryptographicOperations.ZeroMemory(buf); }
    }

    /// <summary>Lokalizált kulcs: Kul = H(Ku || engineID || Ku).</summary>
    public static byte[] Localize(SnmpAuthProto a, byte[] ku, byte[] engineId) => Hash(a, Ber.Concat(ku, engineId, ku));

    public static int PrivKeyLen(SnmpPrivProto p) => p switch
    {
        SnmpPrivProto.Des => 16, SnmpPrivProto.TripleDes => 32, SnmpPrivProto.Aes128 => 16,
        SnmpPrivProto.Aes192 or SnmpPrivProto.Aes192C => 24, SnmpPrivProto.Aes256 or SnmpPrivProto.Aes256C => 32, _ => 0
    };

    /// <summary>
    /// A titkosító kulcs: a lokalizált kulcs eleje; ha rövidebb a szükségesnél, bővítve. Blumenthal (aes192/aes256, mint
    /// a Net-SNMP): Kul' = Kul || H(Kul) ismételve. Reeder (aes192c/aes256c, 3des – Cisco): a lokalizált kulcsot jelszóként
    /// újra kulccsá alakítjuk és lokalizáljuk, majd hozzáfűzzük.
    /// </summary>
    public static byte[] PrivKey(SnmpAuthProto a, SnmpPrivProto p, byte[] privKu, byte[] engineId)
    {
        var need = PrivKeyLen(p);
        var kul = Localize(a, privKu, engineId);
        var ext = kul;
        if (p is SnmpPrivProto.TripleDes or SnmpPrivProto.Aes192C or SnmpPrivProto.Aes256C)
        {
            var last = kul;
            while (ext.Length < need) { last = Localize(a, PasswordToKey(a, last), engineId); ext = Ber.Concat(ext, last); }
        }
        else
            while (ext.Length < need) ext = Ber.Concat(ext, Hash(a, ext));
        return ext[..need];
    }

    private static byte[] Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var r = new byte[a.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    private static byte[] AesIv(int boots, int time, byte[] salt)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(iv, boots);
        BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(4), time);
        salt.CopyTo(iv, 8);
        return iv;
    }

    /// <summary>AES-CFB128 (a .NET CFB-je csak teljes blokkokat fogad - az SNMP-ben a hossz tetszőleges).</summary>
    private static byte[] AesCfb(byte[] key, byte[] iv, ReadOnlySpan<byte> data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var outp = new byte[data.Length];
        var fb = iv.ToArray();
        var ks = new byte[16];
        for (int off = 0; off < data.Length; off += 16)
        {
            aes.EncryptEcb(fb, ks, PaddingMode.None);
            var n = Math.Min(16, data.Length - off);
            for (int i = 0; i < n; i++) outp[off + i] = (byte)(data[off + i] ^ ks[i]);
            if (n == 16) (encrypt ? outp.AsSpan(off, 16) : data.Slice(off, 16)).CopyTo(fb);
        }
        return outp;
    }

    public static byte[] Decrypt(SnmpPrivProto p, byte[] key, byte[] salt, int boots, int time, ReadOnlySpan<byte> data)
    {
        if (salt.Length != 8) throw new SnmpFormatException("érvénytelen privacy-paraméter");
        switch (p)
        {
            case SnmpPrivProto.Des:
            {
                if (data.Length % 8 != 0) throw new SnmpFormatException("DES: nem 8 többszöröse");
                using var des = DES.Create();
                des.Key = key[..8];
                return des.DecryptCbc(data, Xor(key.AsSpan(8, 8), salt), PaddingMode.None);
            }
            case SnmpPrivProto.TripleDes:
            {
                if (data.Length % 8 != 0) throw new SnmpFormatException("3DES: nem 8 többszöröse");
                using var des = TripleDES.Create();
                des.Key = key[..24];
                return des.DecryptCbc(data, Xor(key.AsSpan(24, 8), salt), PaddingMode.None);
            }
            case SnmpPrivProto.None: throw new SnmpFormatException("nincs titkosítás beállítva");
            default: return AesCfb(key, AesIv(boots, time, salt), data, encrypt: false);
        }
    }

    private static long _salt = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));

    /// <summary>Titkosítás a válaszhoz: (titkosított, salt). A salt egyedi (számláló), ahogy az RFC kéri.</summary>
    public static (byte[] Data, byte[] Salt) Encrypt(SnmpPrivProto p, byte[] key, int boots, int time, byte[] plain)
    {
        var salt = new byte[8];
        var ctr = Interlocked.Increment(ref _salt);
        switch (p)
        {
            case SnmpPrivProto.Des:
            case SnmpPrivProto.TripleDes:
            {
                BinaryPrimitives.WriteInt32BigEndian(salt, boots);
                BinaryPrimitives.WriteInt32BigEndian(salt.AsSpan(4), (int)ctr);
                var padded = new byte[(plain.Length + 7) / 8 * 8];
                plain.CopyTo(padded, 0);
                if (p == SnmpPrivProto.Des)
                {
                    using var des = DES.Create(); des.Key = key[..8];
                    return (des.EncryptCbc(padded, Xor(key.AsSpan(8, 8), salt), PaddingMode.None), salt);
                }
                using var tdes = TripleDES.Create(); tdes.Key = key[..24];
                return (tdes.EncryptCbc(padded, Xor(key.AsSpan(24, 8), salt), PaddingMode.None), salt);
            }
            default:
                BinaryPrimitives.WriteInt64BigEndian(salt, ctr);
                return (AesCfb(key, AesIv(boots, time, salt), plain, encrypt: true), salt);
        }
    }
}

/// <summary>SHA-224 és HMAC-SHA-224 (a .NET-ben nincs): a SHA-256 tömörítője más kezdőértékkel, 28 bájtra vágva.</summary>
public static class Sha224
{
    private static readonly uint[] K =
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    };
    private static readonly uint[] Iv224 = { 0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939, 0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4 };
    private static readonly uint[] Iv256 = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };

    public static byte[] Hash(ReadOnlySpan<byte> data) => Core(data, Iv224, 28);
    /// <summary>Csak a tesztnek: a tömörítő ellenőrzése a beépített SHA-256-tal.</summary>
    public static byte[] Sha256ForTest(ReadOnlySpan<byte> data) => Core(data, Iv256, 32);

    private static byte[] Core(ReadOnlySpan<byte> data, uint[] iv, int outLen)
    {
        var h = (uint[])iv.Clone();
        var total = (data.Length + 9 + 63) / 64 * 64;
        var msg = new byte[total];
        data.CopyTo(msg);
        msg[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(total - 8), (ulong)data.Length * 8);
        var w = new uint[64];
        for (int off = 0; off < total; off += 64)
        {
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(off + i * 4));
            for (int i = 16; i < 64; i++)
            {
                var s0 = uint.RotateRight(w[i - 15], 7) ^ uint.RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
                var s1 = uint.RotateRight(w[i - 2], 17) ^ uint.RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (int i = 0; i < 64; i++)
            {
                var S1 = uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25);
                var ch = (e & f) ^ (~e & g);
                var t1 = hh + S1 + ch + K[i] + w[i];
                var S0 = uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22);
                var maj = (a & b) ^ (a & c) ^ (b & c);
                var t2 = S0 + maj;
                hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
        }
        var r = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(i * 4), h[i]);
        return r[..outLen];
    }

    public static byte[] Hmac(byte[] key, ReadOnlySpan<byte> data)
    {
        var k = key.Length > 64 ? Hash(key) : key;
        var ipad = new byte[64]; var opad = new byte[64];
        for (int i = 0; i < 64; i++) { var kb = i < k.Length ? k[i] : (byte)0; ipad[i] = (byte)(kb ^ 0x36); opad[i] = (byte)(kb ^ 0x5c); }
        var inner = Hash(Ber.Concat(ipad, data.ToArray()));
        return Hash(Ber.Concat(opad, inner));
    }
}

/// <summary>Egy fogadott trap / inform a felületnek és a naplónak.</summary>
public sealed class SnmpTrap
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Source { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>v1 / v2c: a közösségnév; v3: a felhasználó és a biztonsági szint.</summary>
    public string Security { get; set; } = "";
    public bool Inform { get; set; }
    public string TrapOid { get; set; } = "";
    public string TrapName { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string AgentAddr { get; set; } = "";
    public List<SnmpVarBind> VarBinds { get; set; } = new();
    public bool Alert { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Egy USM-felhasználó a fogadóban: a jelszavakból egyszer számolt kulcsok (Ku).</summary>
public sealed class SnmpUserKeys
{
    public string Name { get; init; } = "";
    public SnmpAuthProto Auth { get; init; }
    public byte[] AuthKu { get; init; } = Array.Empty<byte>();
    public SnmpPrivProto Priv { get; init; }
    public byte[] PrivKu { get; init; } = Array.Empty<byte>();

    public static SnmpUserKeys Create(string name, SnmpAuthProto auth, string authPassword, SnmpPrivProto priv, string privPassword) => new()
    {
        Name = name, Auth = auth, Priv = auth == SnmpAuthProto.None ? SnmpPrivProto.None : priv,
        AuthKu = auth == SnmpAuthProto.None ? Array.Empty<byte>() : Usm.PasswordToKey(auth, Encoding.UTF8.GetBytes(authPassword)),
        PrivKu = auth == SnmpAuthProto.None || priv == SnmpPrivProto.None ? Array.Empty<byte>() : Usm.PasswordToKey(auth, Encoding.UTF8.GetBytes(privPassword))
    };
}

/// <summary>
/// Az SNMP-motor: egy beérkezett csomag feldolgozása (trap / inform), és ha kell, a válasz (inform: Response; v3
/// felderítés / időszinkron: Report). Szálbiztos. A saját engineID / boots a v3 informhoz kell (ott a fogadó a hiteles fél).
/// </summary>
public sealed class SnmpEngine
{
    public byte[] EngineId { get; }
    public int Boots { get; }
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public int Time => (int)Math.Min(int.MaxValue, _clock.Elapsed.TotalSeconds);

    private readonly Dictionary<string, SnmpUserKeys> _users;
    private readonly List<byte[]> _communities;
    private readonly ConcurrentDictionary<string, (byte[] Auth, byte[] Priv)> _localized = new();
    private readonly ConcurrentDictionary<string, DateTime> _replay = new();

    // ---- számlálók (a felületnek)
    public long UnknownCommunity, UnknownUser, WrongDigest, NotInTime, DecryptErrors, ParseErrors, UnknownEngine, UnsupportedLevel, Replayed;

    public const string UsmStatsUnsupportedSecLevels = "1.3.6.1.6.3.15.1.1.1.0", UsmStatsNotInTimeWindows = "1.3.6.1.6.3.15.1.1.2.0",
        UsmStatsUnknownUserNames = "1.3.6.1.6.3.15.1.1.3.0", UsmStatsUnknownEngineIds = "1.3.6.1.6.3.15.1.1.4.0",
        UsmStatsWrongDigests = "1.3.6.1.6.3.15.1.1.5.0", UsmStatsDecryptionErrors = "1.3.6.1.6.3.15.1.1.6.0";

    public SnmpEngine(byte[] engineId, int boots, IEnumerable<SnmpUserKeys> users, IEnumerable<string> communities)
    {
        EngineId = engineId;
        Boots = boots;
        _users = users.GroupBy(u => u.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _communities = communities.Where(c => c.Length > 0).Select(c => Encoding.UTF8.GetBytes(c)).ToList();
    }

    /// <summary>Új engineID (RFC 3411 5-ös formátum: 0x80 + 0 vállalatszám + 0x05 + 12 véletlen bájt).</summary>
    public static byte[] NewEngineId() => Ber.Concat(new byte[] { 0x80, 0, 0, 0, 0x05 }, RandomNumberGenerator.GetBytes(12));

    public sealed record Result(SnmpTrap? Trap, byte[]? Reply, string? Error);

    public Result Process(byte[] pkt, string source)
    {
        try
        {
            var msg = new BerReader(pkt).Seq("üzenet");
            var ver = msg.Int("verzió");
            if (ver is 0 or 1) return Community(pkt, msg, ver, source);
            if (ver == 3) return V3(pkt, msg, source);
            return new Result(null, null, $"ismeretlen SNMP-verzió ({ver})");
        }
        catch (SnmpFormatException ex) { Interlocked.Increment(ref ParseErrors); return new Result(null, null, "hibás csomag: " + ex.Message); }
        catch (CryptographicException) { Interlocked.Increment(ref DecryptErrors); return new Result(null, null, "visszafejtési hiba"); }
        catch (Exception ex) { Interlocked.Increment(ref ParseErrors); return new Result(null, null, "hibás csomag: " + ex.GetType().Name); }
    }

    private bool CommunityOk(byte[] c) => _communities.Any(x => x.Length == c.Length && CryptographicOperations.FixedTimeEquals(x, c));

    private Result Community(byte[] pkt, BerReader msg, long ver, string source)
    {
        var community = msg.Octets("community");
        if (!CommunityOk(community)) { Interlocked.Increment(ref UnknownCommunity); return new Result(null, null, "ismeretlen közösségnév (community)"); }
        var pdu = SnmpPdu.Parse(msg);
        var cname = Encoding.UTF8.GetString(community);
        if (ver == 0)
        {
            if (pdu.Type != SnmpPdu.TrapV1) return new Result(null, null, "v1: nem trap");
            return new Result(Build(pdu, source, "v1", cname, false), null, null);
        }
        if (pdu.Type is not (SnmpPdu.TrapV2 or SnmpPdu.Inform)) return new Result(null, null, "v2c: nem trap / inform");
        byte[]? reply = null;
        if (pdu.Type == SnmpPdu.Inform)
            reply = Ber.Seq(Ber.Int(1), Ber.Oct(community), Ber.Tlv(SnmpPdu.Response, Ber.Concat(Ber.Int(pdu.RequestId), Ber.Int(0), Ber.Int(0), pdu.RawVarBinds)));
        return new Result(Build(pdu, source, "v2c", cname, pdu.Type == SnmpPdu.Inform), reply, null);
    }

    private (byte[] Auth, byte[] Priv) Keys(SnmpUserKeys u, byte[] engineId)
    {
        var k = u.Name + "|" + Convert.ToHexString(engineId);
        if (_localized.TryGetValue(k, out var v)) return v;
        if (_localized.Count > 2000) _localized.Clear();   // sok különböző engineID-vel ne lehessen a memóriát telerakni
        v = (u.Auth == SnmpAuthProto.None ? Array.Empty<byte>() : Usm.Localize(u.Auth, u.AuthKu, engineId),
             u.Priv == SnmpPrivProto.None ? Array.Empty<byte>() : Usm.PrivKey(u.Auth, u.Priv, u.PrivKu, engineId));
        _localized[k] = v;
        return v;
    }

    private Result V3(byte[] pkt, BerReader msg, string source)
    {
        var gd = msg.Seq("msgGlobalData");
        var msgId = gd.Int("msgID");
        gd.Int("msgMaxSize");
        var flagsB = gd.Octets("msgFlags");
        if (flagsB.Length != 1) throw new SnmpFormatException("msgFlags");
        var flags = flagsB[0];
        if (gd.Int("msgSecurityModel") != 3) return new Result(null, null, "v3: nem USM biztonsági modell");

        var usm = msg.Enter(msg.Expect(0x04, "msgSecurityParameters")).Seq("USM");
        var engineId = usm.Octets("engineID");
        var boots = (int)Math.Clamp(usm.Int("engineBoots"), 0, int.MaxValue);
        var time = (int)Math.Clamp(usm.Int("engineTime"), 0, int.MaxValue);
        var userB = usm.Octets("userName");
        var apT = usm.Expect(0x04, "authParameters");
        var authParams = pkt.AsSpan(apT.Start, apT.Len).ToArray();
        var privParams = usm.Octets("privParameters");
        var user = Encoding.UTF8.GetString(userB);
        bool auth = (flags & 1) != 0, priv = (flags & 2) != 0, reportable = (flags & 4) != 0;
        if (priv && !auth) throw new SnmpFormatException("titkosítás hitelesítés nélkül");
        var dataT = msg.Next();
        var ours = engineId.AsSpan().SequenceEqual(EngineId);

        long PlainReqId()
        {
            try { if (dataT.Tag == 0x30) { var sc = msg.Enter(dataT); sc.Octets("ctx"); sc.Octets("ctx"); return SnmpPdu.Parse(sc).RequestId; } } catch { }
            return 0;
        }

        // Felderítés (a küldő még nem ismeri az engineID-nket): Report usmStatsUnknownEngineIDs, a saját engineID-vel.
        if (engineId.Length == 0)
        {
            Interlocked.Increment(ref UnknownEngine);
            return new Result(null, reportable ? Report(msgId, 0, userB, null, UsmStatsUnknownEngineIds, UnknownEngine, PlainReqId()) : null, null);
        }
        if (!_users.TryGetValue(user, out var u))
        {
            Interlocked.Increment(ref UnknownUser);
            return new Result(null, ours && reportable ? Report(msgId, 0, userB, null, UsmStatsUnknownUserNames, UnknownUser, PlainReqId()) : null, $"ismeretlen v3-felhasználó: {Trim(user)}");
        }
        if (auth != (u.Auth != SnmpAuthProto.None) || priv != (u.Priv != SnmpPrivProto.None))
        {
            Interlocked.Increment(ref UnsupportedLevel);
            return new Result(null, ours && reportable ? Report(msgId, 0, userB, null, UsmStatsUnsupportedSecLevels, UnsupportedLevel, PlainReqId()) : null,
                $"a(z) {Trim(user)} felhasználó más biztonsági szintre van beállítva");
        }

        var keys = Keys(u, engineId);
        if (auth)
        {
            var macLen = Usm.MacLen(u.Auth);
            var copy = pkt.ToArray();
            Array.Clear(copy, apT.Start, apT.Len);
            var mac = Usm.Hmac(u.Auth, keys.Auth, copy).AsSpan(0, macLen);
            if (authParams.Length != macLen || !CryptographicOperations.FixedTimeEquals(mac, authParams))
            {
                Interlocked.Increment(ref WrongDigest);
                return new Result(null, ours && reportable ? Report(msgId, 0, userB, null, UsmStatsWrongDigests, WrongDigest, 0) : null, $"hibás hitelesítés ({Trim(user)}) – rossz jelszó vagy protokoll?");
            }
            if (ours)
            {
                // Mi vagyunk a hiteles fél (inform): az időablak ±150 mp. Eltérésnél hitelesített Report a mi időnkkel – a küldő átáll.
                if (boots != Boots || Math.Abs((long)time - Time) > 150)
                {
                    Interlocked.Increment(ref NotInTime);
                    return new Result(null, reportable ? Report(msgId, 1, userB, u, UsmStatsNotInTimeWindows, NotInTime, 0) : null, null);
                }
            }
            else
            {
                // Trap (a küldő a hiteles fél): ugyanaz az üzenet kétszer nem jöhet be (visszajátszás). Az időablakot nem
                // vizsgáljuk - sok eszköz traphez 0-s engineTime-ot küld.
                var rk = Convert.ToHexString(engineId) + ":" + msgId + ":" + Convert.ToHexString(authParams);
                var now = DateTime.UtcNow;
                if (_replay.Count > 20000) foreach (var old in _replay.Where(x => now - x.Value > TimeSpan.FromMinutes(15)).Select(x => x.Key).ToList()) _replay.TryRemove(old, out _);
                if (!_replay.TryAdd(rk, now)) { Interlocked.Increment(ref Replayed); return new Result(null, null, "ismételt (visszajátszott) üzenet"); }
            }
        }

        BerReader scoped;
        if (priv)
        {
            if (dataT.Tag != 0x04) throw new SnmpFormatException("titkosított adat helyett nyílt");
            byte[] plain;
            try { plain = Usm.Decrypt(u.Priv, keys.Priv, privParams, boots, time, pkt.AsSpan(dataT.Start, dataT.Len)); }
            catch (Exception)
            {
                Interlocked.Increment(ref DecryptErrors);
                return new Result(null, ours && reportable ? Report(msgId, 0, userB, null, UsmStatsDecryptionErrors, DecryptErrors, 0) : null, "visszafejtési hiba (rossz titkosító jelszó / protokoll?)");
            }
            try { scoped = new BerReader(plain).Seq("scopedPDU"); }
            catch (SnmpFormatException)
            {
                Interlocked.Increment(ref DecryptErrors);
                return new Result(null, null, "visszafejtési hiba (rossz titkosító jelszó / protokoll?)");
            }
        }
        else
        {
            if (dataT.Tag != 0x30) throw new SnmpFormatException("scopedPDU");
            scoped = msg.Enter(dataT);
        }
        var ctxEngine = scoped.Octets("contextEngineID");
        var ctxName = scoped.Octets("contextName");
        var pdu = SnmpPdu.Parse(scoped);
        var level = !auth ? "noAuthNoPriv" : priv ? "authPriv" : "authNoPriv";
        if (pdu.Type == SnmpPdu.Inform)
        {
            if (!ours) return new Result(null, reportable ? Report(msgId, 0, userB, null, UsmStatsUnknownEngineIds, UnknownEngine, pdu.RequestId) : null, "inform: idegen engineID");
            var resp = Ber.Seq(Ber.Oct(ctxEngine), Ber.Oct(ctxName),
                Ber.Tlv(SnmpPdu.Response, Ber.Concat(Ber.Int(pdu.RequestId), Ber.Int(0), Ber.Int(0), pdu.RawVarBinds)));
            return new Result(Build(pdu, source, "v3", $"{user} ({level})", true), BuildV3(msgId, flags & 3, userB, auth ? u : null, resp), null);
        }
        if (pdu.Type != SnmpPdu.TrapV2) return new Result(null, null, "v3: nem trap / inform");
        return new Result(Build(pdu, source, "v3", $"{user} ({level})", false), null, null);
    }

    private static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private byte[] Report(long msgId, int flags, byte[] userB, SnmpUserKeys? u, string statOid, long counter, long reqId)
    {
        var vb = Ber.Seq(Ber.Seq(Ber.Oid(statOid), Ber.UInt(0x41, (ulong)Math.Max(0, counter) & 0xFFFFFFFF)));
        var scoped = Ber.Seq(Ber.Oct(EngineId), Ber.Oct(Array.Empty<byte>()), Ber.Tlv(SnmpPdu.Report, Ber.Concat(Ber.Int(reqId), Ber.Int(0), Ber.Int(0), vb)));
        return BuildV3(msgId, flags, userB, u, scoped);
    }

    /// <summary>Saját (hiteles) v3-üzenet: a mi engineID-nkkel, boots / time értékünkkel; hitelesítve / titkosítva, ha kell.</summary>
    private byte[] BuildV3(long msgId, int flags, byte[] userB, SnmpUserKeys? u, byte[] scoped)
    {
        var auth = (flags & 1) != 0 && u is not null;
        var priv = auth && (flags & 2) != 0 && u!.Priv != SnmpPrivProto.None;
        flags = (auth ? 1 : 0) | (priv ? 2 : 0);
        int boots = Boots, time = Time;
        var keys = auth ? Keys(u!, EngineId) : (Array.Empty<byte>(), Array.Empty<byte>());
        var privParams = Array.Empty<byte>();
        var msgData = scoped;
        if (priv)
        {
            var (enc, salt) = Usm.Encrypt(u!.Priv, keys.Item2, boots, time, scoped);
            privParams = salt;
            msgData = Ber.Oct(enc);
        }
        var macLen = auth ? Usm.MacLen(u!.Auth) : 0;
        var e = Ber.Oct(EngineId); var b = Ber.Int(boots); var t = Ber.Int(time); var un = Ber.Oct(userB);
        var ap = Ber.Oct(new byte[macLen]); var pp = Ber.Oct(privParams);
        var usmContent = Ber.Concat(e, b, t, un, ap, pp);
        var usm = Ber.Tlv(0x30, usmContent);
        var global = Ber.Seq(Ber.Int(msgId), Ber.Int(65507), Ber.Oct(new[] { (byte)flags }), Ber.Int(3));
        var ver = Ber.Int(3);
        var body = Ber.Concat(ver, global, Ber.Oct(usm), msgData);
        var whole = Ber.Tlv(0x30, body);
        if (auth)
        {
            var off = Ber.HeaderLen(body.Length) + ver.Length + global.Length + Ber.HeaderLen(usm.Length) + Ber.HeaderLen(usmContent.Length)
                      + e.Length + b.Length + t.Length + un.Length + Ber.HeaderLen(macLen);
            Usm.Hmac(u!.Auth, keys.Item1, whole).AsSpan(0, macLen).CopyTo(whole.AsSpan(off));
        }
        return whole;
    }

    // ---- a trap olvasható formája

    public const string SysUpTime = "1.3.6.1.2.1.1.3.0", SnmpTrapOid = "1.3.6.1.6.3.1.1.4.1.0";

    private static readonly Dictionary<string, string> Known = new()
    {
        ["1.3.6.1.6.3.1.1.5.1"] = "coldStart", ["1.3.6.1.6.3.1.1.5.2"] = "warmStart", ["1.3.6.1.6.3.1.1.5.3"] = "linkDown",
        ["1.3.6.1.6.3.1.1.5.4"] = "linkUp", ["1.3.6.1.6.3.1.1.5.5"] = "authenticationFailure", ["1.3.6.1.6.3.1.1.5.6"] = "egpNeighborLoss",
        ["1.3.6.1.2.1.33.2.1"] = "upsTrapOnBattery", ["1.3.6.1.2.1.33.2.2"] = "upsTrapTestCompleted", ["1.3.6.1.2.1.33.2.3"] = "upsTrapAlarmEntryAdded",
        ["1.3.6.1.2.1.33.2.4"] = "upsTrapAlarmEntryRemoved", ["1.3.6.1.2.1.47.2.0.1"] = "entConfigChange", ["1.3.6.1.2.1.17.0.1"] = "newRoot",
        ["1.3.6.1.2.1.17.0.2"] = "topologyChange", ["1.3.6.1.4.1.8072.2.3.0.1"] = "netSnmpExampleHeartbeat (próba)",
        [SysUpTime] = "sysUpTime.0", [SnmpTrapOid] = "snmpTrapOID.0", ["1.3.6.1.6.3.1.1.4.3.0"] = "snmpTrapEnterprise.0",
        ["1.3.6.1.6.3.18.1.3.0"] = "snmpTrapAddress.0", ["1.3.6.1.6.3.18.1.4.0"] = "snmpTrapCommunity.0", ["1.3.6.1.2.1.1.5.0"] = "sysName.0",
        ["1.3.6.1.2.1.1.1.0"] = "sysDescr.0", ["1.3.6.1.2.1.1.6.0"] = "sysLocation.0",
    };
    private static readonly (string Prefix, string Name)[] KnownColumns =
    {
        ("1.3.6.1.2.1.2.2.1.1", "ifIndex"), ("1.3.6.1.2.1.2.2.1.2", "ifDescr"), ("1.3.6.1.2.1.2.2.1.3", "ifType"),
        ("1.3.6.1.2.1.2.2.1.7", "ifAdminStatus"), ("1.3.6.1.2.1.2.2.1.8", "ifOperStatus"), ("1.3.6.1.2.1.31.1.1.1.1", "ifName"),
        ("1.3.6.1.2.1.31.1.1.1.18", "ifAlias"),
    };

    /// <summary>Ismert név az OID-hez (pl. linkDown, ifDescr.3), különben maga az OID.</summary>
    public static string Name(string oid)
    {
        if (Known.TryGetValue(oid, out var n)) return n;
        foreach (var (p, name) in KnownColumns)
            if (oid.StartsWith(p + ".", StringComparison.Ordinal)) return name + oid[p.Length..];
        return oid;
    }

    private static SnmpTrap Build(SnmpPdu pdu, string source, string version, string security, bool inform)
    {
        var t = new SnmpTrap { AtUtc = DateTime.UtcNow, Source = source, Version = version, Security = security, Inform = inform };
        var vbs = pdu.VarBinds;
        if (pdu.Type == SnmpPdu.TrapV1)
        {
            // RFC 3584 3.1: a v1 trap v2-es trap-OID-ja.
            t.TrapOid = pdu.Generic is >= 0 and < 6 ? "1.3.6.1.6.3.1.1.5." + (pdu.Generic + 1) : pdu.Enterprise + ".0." + pdu.Specific;
            t.Uptime = Ber.Ticks(pdu.TimeStamp);
            t.AgentAddr = pdu.AgentAddr;
            t.VarBinds = vbs.ToList();
        }
        else
        {
            t.Uptime = vbs.FirstOrDefault(v => v.Oid == SysUpTime)?.Value ?? "";
            t.TrapOid = vbs.FirstOrDefault(v => v.Oid == SnmpTrapOid)?.Value ?? "";
            t.VarBinds = vbs.Where(v => v.Oid is not SysUpTime and not SnmpTrapOid).ToList();
        }
        t.TrapName = t.TrapOid.Length > 0 ? Name(t.TrapOid) : "(trap-OID nélkül)";
        var details = t.VarBinds.Take(4).Select(v => $"{Name(v.Oid)}={v.Value}");
        var text = t.TrapName + (t.VarBinds.Count > 0 ? " – " + string.Join("; ", details) : "");
        t.Text = text.Length <= 300 ? text : text[..300] + "…";
        return t;
    }
}
