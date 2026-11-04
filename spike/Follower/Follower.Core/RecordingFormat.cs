using System.Globalization;
using System.Text;

namespace Crosscheck.Follower;

public sealed class RecordingFormatException(string message) : Exception(message);

public sealed record RecordingHeader(DateTime StartUtc, string AircraftTitle, string Note);

/// <summary>A loaded recording. <see cref="Truncated"/>: the last record was cut off (e.g. a crash while recording) and dropped.</summary>
public sealed record Recording(RecordingHeader Header, IReadOnlyList<Frame> Frames, bool Truncated)
{
    public double Duration => Frames.Count < 2 ? 0 : Frames[^1].T - Frames[0].T;

    /// <summary>The part between <paramref name="from"/> and <paramref name="to"/> seconds after the start.</summary>
    public Recording Cut(double? from, double? to)
    {
        if (Frames.Count == 0) return this;
        var t0 = Frames[0].T;
        var kept = Frames.Where(f => (from is null || f.T - t0 >= from) && (to is null || f.T - t0 <= to)).ToArray();
        return this with { Frames = kept };
    }
}

/// <summary>
/// The .cxrec file: a header that describes its own fields, then fixed-size little-endian
/// records (doubles for time and position, float32 for the rest, one byte per bool).
///
/// Header: "CXREC\0", u16 version, i64 start UTC ticks, string aircraft title, string note,
/// u16 field count, then per field: string name, string sim var, string unit, u8 type.
/// Strings are u16 byte length + UTF-8.
/// </summary>
public static class RecordingFormat
{
    public static readonly byte[] Magic = "CXREC\0"u8.ToArray();
    public const ushort Version = 1;

    public static int RecordSize(IEnumerable<FieldSpec> fields) => fields.Sum(f => f.Size);

    public static Recording Load(string path)
    {
        using var s = File.OpenRead(path);
        return Read(s);
    }

    public static void Save(string path, RecordingHeader header, IEnumerable<Frame> frames)
    {
        using var w = new RecordingWriter(File.Create(path), header);
        foreach (var f in frames) w.Append(f);
    }

    public static Recording Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        byte[] magic;
        try { magic = r.ReadBytes(Magic.Length); }
        catch (EndOfStreamException) { throw new RecordingFormatException("not a .cxrec file (too short)"); }
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new RecordingFormatException("not a .cxrec file (wrong magic)");
        RecordingHeader header;
        var fields = new List<(FieldSpec? Spec, FieldType Type)>();
        try
        {
            var version = r.ReadUInt16();
            if (version != Version) throw new RecordingFormatException($"unsupported .cxrec version {version} (this tool reads {Version})");
            var start = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            header = new RecordingHeader(start, ReadString(r), ReadString(r));
            var count = r.ReadUInt16();
            for (var i = 0; i < count; i++)
            {
                var name = ReadString(r);
                ReadString(r); // sim var: informative
                ReadString(r); // unit: informative
                var type = (FieldType)r.ReadByte();
                if (type is not (FieldType.F64 or FieldType.F32 or FieldType.Bool)) throw new RecordingFormatException($"field {name}: unknown type {(byte)type}");
                // A field this version does not know is skipped; a known field must keep its type.
                var spec = Fields.Find(name);
                if (spec is not null && spec.Type != type) throw new RecordingFormatException($"field {name}: type {type}, expected {spec.Type}");
                fields.Add((spec, type));
            }
        }
        catch (EndOfStreamException)
        {
            throw new RecordingFormatException("header cut off");
        }
        if (fields.All(f => f.Spec?.Name != "t")) throw new RecordingFormatException("no time field");

        var size = fields.Sum(f => f.Type switch { FieldType.F64 => 8, FieldType.F32 => 4, _ => 1 });
        var frames = new List<Frame>();
        var buffer = new byte[size];
        var truncated = false;
        while (true)
        {
            var got = ReadFully(stream, buffer);
            if (got == 0) break;
            if (got < size)
            {
                truncated = true;
                break;
            }
            var f = new Frame();
            var pos = 0;
            foreach (var (spec, type) in fields)
            {
                double v;
                switch (type)
                {
                    case FieldType.F64: v = BitConverter.ToDouble(buffer, pos); pos += 8; break;
                    case FieldType.F32: v = BitConverter.ToSingle(buffer, pos); pos += 4; break;
                    default: v = buffer[pos]; pos += 1; break;
                }
                if (spec is not null) f = spec.Set(f, v);
            }
            frames.Add(f);
        }
        return new Recording(header, frames, truncated);
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    internal static string ReadString(BinaryReader r)
    {
        var len = r.ReadUInt16();
        var bytes = r.ReadBytes(len);
        if (bytes.Length < len) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    internal static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > ushort.MaxValue) bytes = bytes[..ushort.MaxValue];
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>All fields as CSV (invariant culture), one row per frame.</summary>
    public static void WriteCsv(TextWriter w, IEnumerable<Frame> frames)
    {
        w.WriteLine(string.Join(',', Fields.All.Select(f => f.Name)));
        foreach (var f in frames)
            w.WriteLine(string.Join(',', Fields.All.Select(s => s.Get(f).ToString("R", CultureInfo.InvariantCulture))));
    }
}

/// <summary>Appends frames to a .cxrec stream. Flushes at least once per second of frame time, so a crash loses at most about a second.</summary>
public sealed class RecordingWriter : IDisposable
{
    private readonly BinaryWriter _w;
    private double? _lastFlushT;
    private bool _disposed;

    public RecordingWriter(Stream stream, RecordingHeader header)
    {
        _w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        _w.Write(RecordingFormat.Magic);
        _w.Write(RecordingFormat.Version);
        _w.Write(header.StartUtc.ToUniversalTime().Ticks);
        RecordingFormat.WriteString(_w, header.AircraftTitle);
        RecordingFormat.WriteString(_w, header.Note);
        _w.Write((ushort)Fields.All.Count);
        foreach (var f in Fields.All)
        {
            RecordingFormat.WriteString(_w, f.Name);
            RecordingFormat.WriteString(_w, f.SimVar);
            RecordingFormat.WriteString(_w, f.Unit);
            _w.Write((byte)f.Type);
        }
        _w.Flush();
    }

    public long Count { get; private set; }

    public void Append(in Frame frame)
    {
        foreach (var f in Fields.All)
        {
            var v = f.Get(frame);
            switch (f.Type)
            {
                case FieldType.F64: _w.Write(v); break;
                case FieldType.F32: _w.Write((float)v); break;
                default: _w.Write((byte)(v >= 0.5 ? 1 : 0)); break;
            }
        }
        Count++;
        if (_lastFlushT is null || frame.T - _lastFlushT >= 1.0 || frame.T < _lastFlushT)
        {
            _w.Flush();
            _lastFlushT = frame.T;
        }
    }

    public void Flush() => _w.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _w.Flush();
        _w.Dispose();
    }
}
