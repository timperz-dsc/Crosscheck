using System.Text;

namespace Crosscheck.Follower.Tests;

public class FormatTests
{
    private static byte[] Write(IEnumerable<Frame> frames, RecordingHeader? header = null)
    {
        using var ms = new MemoryStream();
        using (var w = new RecordingWriter(new NonClosing(ms), header ?? new RecordingHeader(new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), "Fenix A320", "test")))
            foreach (var f in frames) w.Append(f);
        return ms.ToArray();
    }

    [Fact]
    public void Round_trip_keeps_every_field()
    {
        var frames = Make.Circuit.Frames.Where((_, i) => i % 500 == 0).ToArray();
        var rec = RecordingFormat.Read(new MemoryStream(Write(frames)));
        Assert.Equal("Fenix A320", rec.Header.AircraftTitle);
        Assert.Equal("test", rec.Header.Note);
        Assert.False(rec.Truncated);
        Assert.Equal(frames.Length, rec.Frames.Count);
        for (var i = 0; i < frames.Length; i++)
            foreach (var f in Fields.All)
            {
                var (want, got) = (f.Get(frames[i]), f.Get(rec.Frames[i]));
                if (f.Type == FieldType.F64) Assert.Equal(want, got);
                else Assert.Equal(want, got, Math.Max(1e-4, Math.Abs(want) * 1e-6)); // float32
            }
    }

    [Fact]
    public void Record_size_is_compact()
    {
        Assert.Equal(Fields.SimVarCount, Fields.SimVars.Count);
        Assert.Equal(Fields.All.Count, Fields.All.Select(f => f.Name).Distinct().Count());
        // Time and position as doubles, the rest float32, one byte for the bool.
        Assert.Equal(4 * 8 + (Fields.All.Count - 5) * 4 + 1, RecordingFormat.RecordSize(Fields.All));
        Assert.True(RecordingFormat.RecordSize(Fields.All) < 150);
    }

    [Fact]
    public void Rejects_wrong_magic_version_and_cut_header()
    {
        Assert.Throws<RecordingFormatException>(() => RecordingFormat.Read(new MemoryStream("hello world, not a recording"u8.ToArray())));
        var good = Write([Make.Moving(0, 1, 0)]);
        var badVersion = (byte[])good.Clone();
        badVersion[RecordingFormat.Magic.Length] = 99;
        Assert.Contains("version", Assert.Throws<RecordingFormatException>(() => RecordingFormat.Read(new MemoryStream(badVersion))).Message);
        Assert.Throws<RecordingFormatException>(() => RecordingFormat.Read(new MemoryStream(good[..20])));
        Assert.Throws<RecordingFormatException>(() => RecordingFormat.Read(new MemoryStream([])));
    }

    [Fact]
    public void A_cut_off_last_record_is_dropped_and_reported()
    {
        var bytes = Write(Make.Track(1, 10, 5, 0));
        var cut = bytes[..^7];
        var rec = RecordingFormat.Read(new MemoryStream(cut));
        Assert.True(rec.Truncated);
        Assert.Equal(10, rec.Frames.Count); // 11 written, the last one cut
    }

    [Fact]
    public void Unknown_fields_are_skipped()
    {
        // A file from a later version with an extra field "extra" (F32) after the known ones.
        var frame = Make.Moving(1, 3, 4);
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(RecordingFormat.Magic);
        w.Write(RecordingFormat.Version);
        w.Write(0L);
        RecordingFormat.WriteString(w, "x");
        RecordingFormat.WriteString(w, "");
        w.Write((ushort)(Fields.All.Count + 1));
        foreach (var f in Fields.All)
        {
            RecordingFormat.WriteString(w, f.Name);
            RecordingFormat.WriteString(w, f.SimVar);
            RecordingFormat.WriteString(w, f.Unit);
            w.Write((byte)f.Type);
        }
        RecordingFormat.WriteString(w, "extra");
        RecordingFormat.WriteString(w, "");
        RecordingFormat.WriteString(w, "");
        w.Write((byte)FieldType.F32);
        foreach (var f in Fields.All)
        {
            var v = f.Get(frame);
            if (f.Type == FieldType.F64) w.Write(v);
            else if (f.Type == FieldType.F32) w.Write((float)v);
            else w.Write((byte)v);
        }
        w.Write(123f);
        w.Flush();
        var rec = RecordingFormat.Read(new MemoryStream(ms.ToArray()));
        Assert.Single(rec.Frames);
        Assert.Equal(frame.Lat, rec.Frames[0].Lat);
    }

    [Fact]
    public void Csv_has_a_header_and_one_row_per_frame()
    {
        var sw = new StringWriter();
        RecordingFormat.WriteCsv(sw, Make.Track(1, 4, 1, 0));
        var lines = sw.ToString().Trim().Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.StartsWith("t,lat,lon,alt_ft", lines[0]);
    }

    [Fact]
    public void Cut_keeps_the_given_window()
    {
        var rec = new Recording(new RecordingHeader(default, "", ""), Make.Track(10, 10, 1, 0), false);
        var cut = rec.Cut(2, 5);
        Assert.Equal(2, cut.Frames[0].T, 9);
        Assert.Equal(5, cut.Frames[^1].T, 9);
    }

    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

public class GeoTests
{
    [Fact]
    public void Offset_and_move_round_trip_to_a_millimetre()
    {
        var from = new Frame { Lat = 59.65, Lon = 17.93, AltFt = 1500 };
        foreach (var d in new[] { new Enu(300, -200, 50), new Enu(-1, 1, 0), new Enu(5000, 5000, -400) })
        {
            var (lat, lon, alt) = Geo.Move(from, d);
            Assert.True((Geo.Offset(from, lat, lon, alt) - d).Length < 0.001);
        }
    }

    [Fact]
    public void Offset_matches_known_distances()
    {
        var a = new Frame { Lat = 0, Lon = 0 };
        Assert.InRange(Geo.Offset(a, 0, 1, 0).E, 111_319, 111_320); // 1° of longitude on the equator
        Assert.InRange(Geo.Offset(a, 1, 0, 0).N, 110_570, 110_580); // 1° of latitude there
        Assert.Equal(304.8, Geo.Offset(a, 0, 0, 1000).U, 6);
    }

    [Fact]
    public void Longitude_wraps_at_the_date_line()
    {
        var a = new Frame { Lat = 10, Lon = 179.9999 };
        Assert.True(Math.Abs(Geo.Offset(a, 10, -179.9999, 0).E - 21.9) < 0.1);
    }

    [Theory]
    [InlineData(359, 1, 2)]
    [InlineData(1, 359, -2)]
    [InlineData(180, -180, 0)]
    [InlineData(10, 200, -170)]
    public void Angle_difference_takes_the_short_way(double a, double b, double want) => Assert.Equal(want, Geo.AngleDiff(a, b), 9);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(359.5, -5, 30)]
    [InlineData(123, 12, -25)]
    [InlineData(270, -60, 170)]
    public void Quaternion_round_trip(double h, double p, double b)
    {
        var (h2, p2, b2) = Quat.FromEuler(h, p, b).ToEuler();
        Assert.Equal(0, Geo.AngleDiff(h, h2), 6);
        Assert.Equal(p, p2, 6);
        Assert.Equal(0, Geo.AngleDiff(b, b2), 6);
    }

    [Fact]
    public void Attitude_error_is_the_angle_between()
    {
        Assert.Equal(2, Quat.FromEuler(359, 0, 0).AngleTo(Quat.FromEuler(1, 0, 0)), 6);
        Assert.Equal(5, Quat.FromEuler(90, 3, 0).AngleTo(Quat.FromEuler(90, -2, 0)), 6);
    }

    [Fact]
    public void Slerp_crosses_north_the_short_way()
    {
        var (h, _, _) = Quat.Slerp(Quat.FromEuler(350, 0, 0), Quat.FromEuler(10, 0, 0), 0.5).ToEuler();
        Assert.Equal(0, Geo.AngleDiff(0, h), 6);
        var (h2, _, _) = Quat.Slerp(Quat.FromEuler(350, 0, 0), Quat.FromEuler(10, 0, 0), 0.25).ToEuler();
        Assert.Equal(355, h2, 6);
    }

    [Fact]
    public void Pow_and_log_agree()
    {
        var q = Quat.FromEuler(30, 10, -20);
        Assert.True(q.Pow(0.5).Pow(2).AngleTo(q) < 1e-9);
        Assert.True(q.Pow(0).AngleTo(Quat.Identity) < 1e-9);
    }
}
