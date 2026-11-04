using System.Globalization;

namespace Crosscheck.Follower;

/// <summary>One frame after the handover-test release: the follower as read back, against the recording.</summary>
public sealed record HandoverRow
{
    /// <summary>Seconds since the release (player clock).</summary>
    public double T { get; init; }
    public double GsKt { get; init; }
    public double TruthGsKt { get; init; }
    public double HeadingDeg { get; init; }
    public double TruthHeadingDeg { get; init; }
    /// <summary>Distance moved since the frame before, m.</summary>
    public double StepM { get; init; }
    /// <summary>Distance the ground speed explains for that frame, m.</summary>
    public double ExpectedStepM { get; init; }
    public double PosErrM { get; init; }
    public double AlongErrM { get; init; }
    public double CrossErrM { get; init; }
    public double VertErrM { get; init; }
    public bool OnGround { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
    public double AltFt { get; init; }
}

public sealed record HandoverSummary
{
    /// <summary>Recording time of the release, seconds into the recording.</summary>
    public double AtS { get; init; }
    public int Frames { get; init; }
    public double LoggedS { get; init; }
    public double GsAtReleaseKt { get; init; }
    public double GsAfter1sKt { get; init; }
    public double GsAtEndKt { get; init; }
    public double GsMinKt { get; init; }
    public double GsMaxKt { get; init; }
    /// <summary>The recording's ground speed at the end of the log, kt (what the PF did).</summary>
    public double TruthGsAtEndKt { get; init; }
    /// <summary>Heading change over the log, degrees (follower; the recording's in <see cref="TruthHeadingChangeDeg"/>).</summary>
    public double HeadingChangeDeg { get; init; }
    public double TruthHeadingChangeDeg { get; init; }
    /// <summary>Largest |step − what the ground speed explains| in one frame, m: a jump.</summary>
    public double MaxJumpM { get; init; }
    /// <summary>Largest vertical change in one frame, m (a hop or a drop).</summary>
    public double MaxVertStepM { get; init; }
    public double PosErrAtEndM { get; init; }
    public bool LeftTheGround { get; init; }
}

/// <summary>
/// Logs the follower for <see cref="HandoverTest.LogS"/> after a handover test released the
/// freezes: does it keep its speed and heading, or does it jump or stop?
/// </summary>
public sealed class HandoverLog(IReadOnlyList<Frame> truth, double releaseRecordingT, double atS)
{
    private readonly List<HandoverRow> _rows = [];
    private double _releaseNow = double.NaN;
    private Frame? _last;
    private double _lastNow;
    private double _maxVertStep;

    public IReadOnlyList<HandoverRow> Rows => _rows;
    public bool Done => _rows.Count > 0 && _rows[^1].T >= HandoverTest.LogS;

    /// <summary>One sim frame; the first call is the frame of the release.</summary>
    public void Add(double now, in Frame actual)
    {
        if (double.IsNaN(_releaseNow)) _releaseNow = now;
        var t = now - _releaseNow;
        var tr = Interpolator.Sample(truth, releaseRecordingT + t);
        double step = 0, expected = 0;
        if (_last is { } last)
        {
            var d = Geo.Offset(last, actual);
            step = Math.Sqrt(d.E * d.E + d.N * d.N);
            expected = (last.GroundSpeedKt + actual.GroundSpeedKt) / 2 * Geo.KtToMs * (now - _lastNow);
            _maxVertStep = Math.Max(_maxVertStep, Math.Abs(d.U));
        }
        var e = Geo.Offset(tr, actual);
        var v = Geo.Velocity(tr);
        var vh = Math.Sqrt(v.E * v.E + v.N * v.N);
        var (along, cross) = vh > 0.5 ? ((e.E * v.E + e.N * v.N) / vh, (e.N * v.E - e.E * v.N) / vh) : (0, Math.Sqrt(e.E * e.E + e.N * e.N));
        _rows.Add(new HandoverRow
        {
            T = t, GsKt = actual.GroundSpeedKt, TruthGsKt = tr.GroundSpeedKt, HeadingDeg = actual.HeadingDeg, TruthHeadingDeg = tr.HeadingDeg,
            StepM = step, ExpectedStepM = expected, PosErrM = e.Length, AlongErrM = along, CrossErrM = cross, VertErrM = e.U,
            OnGround = actual.OnGround, Lat = actual.Lat, Lon = actual.Lon, AltFt = actual.AltFt,
        });
        _last = actual;
        _lastNow = now;
    }

    public HandoverSummary? Summarize()
    {
        if (_rows.Count == 0) return null;
        var first = _rows[0];
        var end = _rows[^1];
        var after1 = _rows.FirstOrDefault(r => r.T >= 1) ?? end;
        return new HandoverSummary
        {
            AtS = atS,
            Frames = _rows.Count,
            LoggedS = end.T,
            GsAtReleaseKt = first.GsKt,
            GsAfter1sKt = after1.GsKt,
            GsAtEndKt = end.GsKt,
            GsMinKt = _rows.Min(r => r.GsKt),
            GsMaxKt = _rows.Max(r => r.GsKt),
            TruthGsAtEndKt = end.TruthGsKt,
            HeadingChangeDeg = Geo.AngleDiff(first.HeadingDeg, end.HeadingDeg),
            TruthHeadingChangeDeg = Geo.AngleDiff(first.TruthHeadingDeg, end.TruthHeadingDeg),
            MaxJumpM = _rows.Skip(1).Select(r => Math.Abs(r.StepM - r.ExpectedStepM)).DefaultIfEmpty(0).Max(),
            MaxVertStepM = _maxVertStep,
            PosErrAtEndM = end.PosErrM,
            LeftTheGround = _rows.Any(r => !r.OnGround),
        };
    }

    public void WriteCsv(TextWriter w)
    {
        var c = CultureInfo.InvariantCulture;
        w.WriteLine("t,gs_kt,truth_gs_kt,heading_deg,truth_heading_deg,step_m,expected_step_m,pos_err_m,along_err_m,cross_err_m,vert_err_m,on_ground,lat,lon,alt_ft");
        foreach (var r in _rows)
            w.WriteLine(string.Join(',',
                r.T.ToString("0.0000", c), r.GsKt.ToString("0.00", c), r.TruthGsKt.ToString("0.00", c), r.HeadingDeg.ToString("0.00", c),
                r.TruthHeadingDeg.ToString("0.00", c), r.StepM.ToString("0.000", c), r.ExpectedStepM.ToString("0.000", c), r.PosErrM.ToString("0.000", c),
                r.AlongErrM.ToString("0.000", c), r.CrossErrM.ToString("0.000", c), r.VertErrM.ToString("0.000", c), r.OnGround ? 1 : 0,
                r.Lat.ToString("0.00000000", c), r.Lon.ToString("0.00000000", c), r.AltFt.ToString("0.00", c)));
    }
}
