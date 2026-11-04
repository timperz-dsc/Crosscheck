namespace Crosscheck.Follower;

public enum Phase
{
    /// <summary>On the ground, below 1 kt.</summary>
    Gate,
    /// <summary>On the ground, below 40 kt.</summary>
    Taxi,
    /// <summary>On the ground, 40 kt or more (takeoff and landing roll).</summary>
    Runway,
    Flight,
}

public sealed record MetricsOptions
{
    /// <summary>Acceleration difference (follower minus recording) counted as a jitter spike, m/s².</summary>
    public double SpikeAccel { get; init; } = 2;
    /// <summary>Angular acceleration difference counted as a spike, deg/s².</summary>
    public double SpikeAngAccel { get; init; } = 20;
    /// <summary>Follower sidestick/yoke counts as neutral below this (yoke -1..1; Fenix vars divided by <see cref="FenixStickScale"/>).</summary>
    public double StickNeutral { get; init; } = 0.05;
    /// <summary>Full deflection of the Fenix N_FC_SIDESTICK_* vars (check with `inspect` on a recording).</summary>
    public double FenixStickScale { get; init; } = 1;
    /// <summary>A follower surface this far from the recorded one, with its own stick neutral, is the aircraft reacting, deg.</summary>
    public double SurfaceReactionDeg { get; init; } = 2;
    /// <summary>
    /// True when the sim moves the aircraft on from what was written during the next frame
    /// (method A, and the offline sim). False when it holds it there (B: frozen, C: slew).
    /// Decides what the read-back is compared against.
    /// </summary>
    public bool WritesAdvance { get; init; } = true;
}

/// <summary>One row of frames.csv: the follower as read back from the sim, against the recording.</summary>
public sealed record FrameRow
{
    public double Now { get; init; }
    public double FrameMs { get; init; }
    /// <summary>Recording time the read-back should match.</summary>
    public double TruthT { get; init; }
    public Phase Phase { get; init; }
    public PlayoutStatus Status { get; init; }
    public double LateMs { get; init; }
    public double LagMs { get; init; }
    public double PosErrM { get; init; }
    /// <summary>Along the recorded track: mostly timing.</summary>
    public double AlongErrM { get; init; }
    /// <summary>Across the recorded track: what you see out of the window.</summary>
    public double CrossErrM { get; init; }
    public double VertErrM { get; init; }
    public double AttErrDeg { get; init; }
    /// <summary>Read-back against what was written: how well the sim obeyed the method.</summary>
    public double CmdPosErrM { get; init; }
    public double CmdAttErrDeg { get; init; }
    /// <summary>|follower acceleration - recording acceleration|, m/s². NaN for the first two frames.</summary>
    public double AccelResid { get; init; } = double.NaN;
    public double AngAccelResid { get; init; } = double.NaN;
    public bool Spike { get; init; }
    public bool StickNeutral { get; init; }
    public bool Reaction { get; init; }
    public double ElevDiffDeg { get; init; }
    public double AilDiffDeg { get; init; }
    public double RudDiffDeg { get; init; }
    /// <summary>The follower's GROUND VELOCITY as read back, kt.</summary>
    public double GsKt { get; init; }
    /// <summary>The recording's ground speed at <see cref="TruthT"/>, kt.</summary>
    public double TruthGsKt { get; init; }
    /// <summary>|change of the follower's ground speed − change of the recording's| since the frame before, kt. NaN for the first frame.</summary>
    public double GsJumpKt { get; init; } = double.NaN;
    /// <summary>PC stopwatch time since the frame before, ms (NaN offline). Frame times above are on the sim clock.</summary>
    public double WallMs { get; init; } = double.NaN;
}

public sealed record Stats(double Mean, double P95, double P99, double Max)
{
    public static readonly Stats Empty = new(double.NaN, double.NaN, double.NaN, double.NaN);

    public static Stats Of(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (v.Length == 0) return Empty;
        double P(double q) => v[Math.Min(v.Length - 1, (int)Math.Ceiling(q * v.Length) - 1)];
        return new Stats(v.Average(), P(0.95), P(0.99), v[^1]);
    }
}

public sealed record PhaseSummary
{
    public string Phase { get; init; } = "";
    public int Frames { get; init; }
    public double Seconds { get; init; }
    public Stats PosErrM { get; init; } = Stats.Empty;
    public Stats CrossErrM { get; init; } = Stats.Empty;
    public Stats VertErrM { get; init; } = Stats.Empty;
    public Stats AlongErrM { get; init; } = Stats.Empty;
    public Stats AttErrDeg { get; init; } = Stats.Empty;
    /// <summary>Attitude error without the frames where the aircraft reacted on its own.</summary>
    public Stats AttErrNoReactionDeg { get; init; } = Stats.Empty;
    public Stats CmdPosErrM { get; init; } = Stats.Empty;
    public Stats CmdAttErrDeg { get; init; } = Stats.Empty;
    public Stats AccelResid { get; init; } = Stats.Empty;
    public Stats AngAccelResid { get; init; } = Stats.Empty;
    public int Spikes { get; init; }
    public double SpikesPerMin { get; init; }
    public int Extrapolated { get; init; }
    public int Holding { get; init; }
    public int ReactionFrames { get; init; }
    public int StickTouchedFrames { get; init; }
    public double LagMeanMs { get; init; }
    public Stats FrameMs { get; init; } = Stats.Empty;
    /// <summary>|follower ground speed − recording ground speed|, kt.</summary>
    public Stats GsErrKt { get; init; } = Stats.Empty;
    public Stats GsJumpKt { get; init; } = Stats.Empty;
    public Stats WallMs { get; init; } = Stats.Empty;
}

public sealed record MetricsSummary
{
    public IReadOnlyList<PhaseSummary> Phases { get; init; } = [];
    public PhaseSummary All { get; init; } = new();
    public double LongestLateMs { get; init; }
    public int LateStretches { get; init; }
}

/// <summary>
/// Collects the per-frame metrics. Call <see cref="Add"/> once per sim frame with what was
/// written this frame and the follower as read back this frame (which shows the previous write).
/// </summary>
public sealed class MetricsCollector(IReadOnlyList<Frame> truth, MetricsOptions options, Tangents tangents = Tangents.Velocity)
{
    private readonly List<FrameRow> _rows = [];
    private (double Now, PlayoutResult Target)? _prev;
    private Frame? _writtenBefore; // what was written the frame before _prev
    // Last two (time, follower, truth) samples for the second differences.
    private readonly List<(double Now, Frame Actual, Frame Truth)> _hist = [];
    private double _lateStart = double.NaN;
    private double _longestLate;
    private int _lateStretches;
    private (double Gs, double TruthGs)? _lastGs;

    public MetricsOptions Options { get; } = options;
    public IReadOnlyList<FrameRow> Rows => _rows;

    /// <param name="now">Session time on the player's clock (sim time when available).</param>
    /// <param name="wallMs">PC stopwatch time since the last call, ms (for the report only).</param>
    public void Add(double now, PlayoutResult target, in Frame actual, double wallMs = double.NaN)
    {
        var prev = _prev;
        var writtenBefore = _writtenBefore;
        _prev = (now, target);
        _writtenBefore = prev?.Target.State;
        if (prev is not { } p || p.Target.Status is PlayoutStatus.Empty or PlayoutStatus.Waiting)
        {
            _hist.Clear();
            _lastGs = null;
            return;
        }

        var dt = now - p.Now;
        var written = p.Target.State;
        var truthT = p.Target.RenderTime + (Options.WritesAdvance ? dt : 0);
        var expected = Options.WritesAdvance ? Interpolator.Extrapolate(written, writtenBefore, written.T + dt) : written;
        var tr = Interpolator.Sample(truth, truthT, tangents);

        var d = Geo.Offset(tr, actual);
        var v = Geo.Velocity(tr);
        var vh = Math.Sqrt(v.E * v.E + v.N * v.N);
        double along, cross;
        if (vh > 0.5)
        {
            along = (d.E * v.E + d.N * v.N) / vh;
            cross = (d.N * v.E - d.E * v.N) / vh;
        }
        else
        {
            // Standing still: no track; call it all cross-track.
            along = 0;
            cross = d.Horizontal;
        }

        var neutral = Neutral(actual);
        var elev = actual.ElevatorDeg - tr.ElevatorDeg;
        var ail = actual.AileronDeg - tr.AileronDeg;
        var rud = actual.RudderDeg - tr.RudderDeg;
        var reaction = neutral && Math.Max(Math.Abs(elev), Math.Max(Math.Abs(ail), Math.Abs(rud))) > Options.SurfaceReactionDeg;

        double accel = double.NaN, angAccel = double.NaN;
        _hist.Add((now, actual, tr));
        if (_hist.Count > 3) _hist.RemoveAt(0);
        if (_hist.Count == 3)
        {
            var (a0, a1, a2) = (_hist[0], _hist[1], _hist[2]);
            var fa = Accel(a1.Actual, a0.Actual, a2.Actual, a0.Now, a1.Now, a2.Now);
            var ta = Accel(a1.Truth, a0.Truth, a2.Truth, a0.Truth.T, a1.Truth.T, a2.Truth.T);
            accel = (fa - ta).Length;
            var fw = AngAccel(a0.Actual, a1.Actual, a2.Actual, a0.Now, a1.Now, a2.Now);
            var tw = AngAccel(a0.Truth, a1.Truth, a2.Truth, a0.Truth.T, a1.Truth.T, a2.Truth.T);
            angAccel = Math.Sqrt(Sq(fw.X - tw.X) + Sq(fw.Y - tw.Y) + Sq(fw.Z - tw.Z)) * 180 / Math.PI;
        }

        var status = p.Target.Status;
        if (status is PlayoutStatus.Extrapolated or PlayoutStatus.Holding)
        {
            if (double.IsNaN(_lateStart))
            {
                _lateStart = p.Now;
                _lateStretches++;
            }
            _longestLate = Math.Max(_longestLate, now - _lateStart);
        }
        else _lateStart = double.NaN;

        var gsJump = _lastGs is { } lg ? Math.Abs((actual.GroundSpeedKt - lg.Gs) - (tr.GroundSpeedKt - lg.TruthGs)) : double.NaN;
        _lastGs = (actual.GroundSpeedKt, tr.GroundSpeedKt);

        _rows.Add(new FrameRow
        {
            Now = now,
            FrameMs = dt * 1000,
            TruthT = truthT,
            Phase = PhaseOf(tr),
            Status = status,
            LateMs = p.Target.LateBy * 1000,
            LagMs = (now - truthT) * 1000,
            PosErrM = d.Length,
            AlongErrM = along,
            CrossErrM = cross,
            VertErrM = d.U,
            AttErrDeg = Geo.AttitudeError(tr, actual),
            CmdPosErrM = Geo.Offset(expected, actual).Length,
            CmdAttErrDeg = Geo.AttitudeError(expected, actual),
            AccelResid = accel,
            AngAccelResid = angAccel,
            Spike = accel > Options.SpikeAccel || angAccel > Options.SpikeAngAccel,
            StickNeutral = neutral,
            Reaction = reaction,
            ElevDiffDeg = elev,
            AilDiffDeg = ail,
            RudDiffDeg = rud,
            GsKt = actual.GroundSpeedKt,
            TruthGsKt = tr.GroundSpeedKt,
            GsJumpKt = gsJump,
            WallMs = wallMs,
        });
    }

    public static Phase PhaseOf(in Frame f) =>
        !f.OnGround ? Phase.Flight : f.GroundSpeedKt < 1 ? Phase.Gate : f.GroundSpeedKt < 40 ? Phase.Taxi : Phase.Runway;

    public bool Neutral(in Frame f)
    {
        var n = Options.StickNeutral;
        var s = Options.FenixStickScale <= 0 ? 1 : Options.FenixStickScale;
        return Math.Abs(f.YokeX) < n && Math.Abs(f.YokeY) < n
            && Math.Abs(f.FenixStickCaptPitch / s) < n && Math.Abs(f.FenixStickCaptBank / s) < n
            && Math.Abs(f.FenixStickFoPitch / s) < n && Math.Abs(f.FenixStickFoBank / s) < n;
    }

    public MetricsSummary Summarize()
    {
        var phases = Enum.GetValues<Phase>()
            .Select(ph => Summarize(ph.ToString(), _rows.Where(r => r.Phase == ph).ToArray()))
            .Where(s => s.Frames > 0)
            .ToArray();
        return new MetricsSummary
        {
            Phases = phases,
            All = Summarize("All", _rows),
            LongestLateMs = _longestLate * 1000,
            LateStretches = _lateStretches,
        };
    }

    private static PhaseSummary Summarize(string name, IReadOnlyList<FrameRow> rows)
    {
        var seconds = rows.Sum(r => r.FrameMs) / 1000;
        var spikes = rows.Count(r => r.Spike);
        return new PhaseSummary
        {
            Phase = name,
            Frames = rows.Count,
            Seconds = seconds,
            PosErrM = Stats.Of(rows.Select(r => r.PosErrM)),
            CrossErrM = Stats.Of(rows.Select(r => Math.Abs(r.CrossErrM))),
            VertErrM = Stats.Of(rows.Select(r => Math.Abs(r.VertErrM))),
            AlongErrM = Stats.Of(rows.Select(r => Math.Abs(r.AlongErrM))),
            AttErrDeg = Stats.Of(rows.Select(r => r.AttErrDeg)),
            AttErrNoReactionDeg = Stats.Of(rows.Where(r => !r.Reaction).Select(r => r.AttErrDeg)),
            CmdPosErrM = Stats.Of(rows.Select(r => r.CmdPosErrM)),
            CmdAttErrDeg = Stats.Of(rows.Select(r => r.CmdAttErrDeg)),
            AccelResid = Stats.Of(rows.Select(r => r.AccelResid)),
            AngAccelResid = Stats.Of(rows.Select(r => r.AngAccelResid)),
            Spikes = spikes,
            SpikesPerMin = seconds > 0 ? spikes / (seconds / 60) : 0,
            Extrapolated = rows.Count(r => r.Status == PlayoutStatus.Extrapolated),
            Holding = rows.Count(r => r.Status == PlayoutStatus.Holding),
            ReactionFrames = rows.Count(r => r.Reaction),
            StickTouchedFrames = rows.Count(r => !r.StickNeutral),
            LagMeanMs = rows.Count > 0 ? rows.Average(r => r.LagMs) : double.NaN,
            FrameMs = Stats.Of(rows.Select(r => r.FrameMs)),
            GsErrKt = Stats.Of(rows.Select(r => Math.Abs(r.GsKt - r.TruthGsKt))),
            GsJumpKt = Stats.Of(rows.Select(r => r.GsJumpKt)),
            WallMs = Stats.Of(rows.Select(r => r.WallMs)),
        };
    }

    private static double Sq(double x) => x * x;

    /// <summary>Second difference of position at the middle sample, with uneven time steps (m/s²).</summary>
    private static Enu Accel(in Frame mid, in Frame a, in Frame b, double ta, double tm, double tb)
    {
        double h1 = tm - ta, h2 = tb - tm;
        if (h1 <= 1e-9 || h2 <= 1e-9) return default;
        var p0 = Geo.Offset(mid, a);
        var p2 = Geo.Offset(mid, b);
        return (p2 * (1 / h2) + p0 * (1 / h1)) * (2 / (h1 + h2));
    }

    /// <summary>Change of angular velocity over the three samples (rad/s²).</summary>
    private static (double X, double Y, double Z) AngAccel(in Frame a, in Frame m, in Frame b, double ta, double tm, double tb)
    {
        double h1 = tm - ta, h2 = tb - tm;
        if (h1 <= 1e-9 || h2 <= 1e-9) return default;
        var (x1, y1, z1) = (Geo.Attitude(m) * Geo.Attitude(a).Conjugate).Log();
        var (x2, y2, z2) = (Geo.Attitude(b) * Geo.Attitude(m).Conjugate).Log();
        var k = 2 / (h1 + h2);
        return ((x2 / h2 - x1 / h1) * k, (y2 / h2 - y1 / h1) * k, (z2 / h2 - z1 / h1) * k);
    }
}
