// Ported from the CRO-17 follower spike (its Follower.Core/Synthetic.cs). Used by the fake physics
// link (--sim fake) and the tests: a scripted full circuit whose positions are integrated from its velocities.

namespace Crosscheck.Bridge.Follow;

/// <summary>
/// A synthetic full circuit: parked, taxi with a 90° turn, line-up, takeoff, a left circuit at 1500 ft,
/// approach, landing, rollout, turn-off, taxi, stopped. Angles use the sim's signs (pitch negative nose up).
/// </summary>
public static class Synthetic
{
    public const double RefLat = 59.65;
    public const double RefLon = 17.93;
    public const double GroundFt = 137;
    private const double G = 9.80665;

    private sealed class S
    {
        public double T, Lat = RefLat, Lon = RefLon, AltFt = GroundFt, Hdg = 90, Gs, Vs, Pitch, Bank, Turn;
        public double GsRate, VsRate, PitchRate, BankRate, TurnRateRate;
        public bool OnGround = true;
        public double Agl => AltFt - GroundFt;
        public double Kt => Gs / Geo.KtToMs;
    }

    private sealed record Step(string Name, Func<S, double, double, bool> Done)
    {
        public double GsKt { get; init; }
        public double AccelKt { get; init; } = 1;
        public double TurnRate { get; init; }
        public double BankDeg { get; init; }
        public double VsFpm { get; init; }
        public double PitchDeg { get; init; }
        public bool Liftoff { get; init; }
    }

    private static List<PhysicsFrame>? _cached;

    /// <summary>The circuit at 60 frames per second (cached; about 10 minutes).</summary>
    public static IReadOnlyList<PhysicsFrame> Circuit() => _cached ??= Build(60);

    /// <summary>Time of the first frame in the air, seconds.</summary>
    public static double LiftoffT(IReadOnlyList<PhysicsFrame> frames) => frames.First(f => !f.OnGround).T;

    /// <summary>The frames from <paramref name="fromT"/> on, re-timed to start at 0.</summary>
    public static List<PhysicsFrame> From(IReadOnlyList<PhysicsFrame> frames, double fromT) =>
        frames.Where(f => f.T >= fromT).Select(f => f with { T = f.T - fromT }).ToList();

    private static List<PhysicsFrame> Build(double fps)
    {
        Step[] script =
        [
            new("parked", (s, t, _) => t >= 20),
            new("taxi out", (s, t, _) => t >= 40) { GsKt = 15 },
            new("taxi turn", (s, t, turned) => turned >= 87) { GsKt = 15, TurnRate = 3 },
            new("taxi straight", (s, t, _) => t >= 30) { GsKt = 15 },
            new("line-up turn", (s, t, turned) => turned >= 87) { GsKt = 10, TurnRate = -3 },
            new("hold on runway", (s, t, _) => s.Gs == 0 && t >= 12) { GsKt = 0, AccelKt = 2 },
            new("takeoff roll", (s, t, _) => s.Kt >= 145) { GsKt = 165, AccelKt = 4 },
            new("rotate", (s, t, _) => !s.OnGround && s.Agl >= 35) { GsKt = 165, AccelKt = 3, PitchDeg = 12, VsFpm = 1500, Liftoff = true },
            new("initial climb", (s, t, _) => s.Agl >= 1000) { GsKt = 170, PitchDeg = 10, VsFpm = 1800 },
            new("climb", (s, t, _) => s.Agl >= 1450) { GsKt = 180, PitchDeg = 8, VsFpm = 1200 },
            new("crosswind turn", (s, t, turned) => turned >= 172) { GsKt = 180, PitchDeg = 3, BankDeg = -25 },
            new("downwind", (s, t, _) => t >= 60) { GsKt = 160, PitchDeg = 3 },
            new("base turn", (s, t, turned) => turned >= 172) { GsKt = 150, PitchDeg = 2, BankDeg = -20, VsFpm = -500 },
            new("final", (s, t, _) => s.Agl <= 30) { GsKt = 140, PitchDeg = 3, VsFpm = -750 },
            new("flare", (s, t, _) => s.OnGround) { GsKt = 135, PitchDeg = 5, VsFpm = -150 },
            new("rollout", (s, t, _) => s.Kt <= 16) { GsKt = 15, AccelKt = 4 },
            new("turn off", (s, t, turned) => turned >= 87) { GsKt = 15, TurnRate = 3 },
            new("taxi in", (s, t, _) => t >= 30) { GsKt = 15 },
            new("stop", (s, t, _) => s.Gs == 0 && t >= 10) { GsKt = 0, AccelKt = 2 },
        ];

        var dt = 1 / fps;
        var s = new S();
        var frames = new List<PhysicsFrame> { ToFrame(s) };
        foreach (var step in script)
        {
            var start = s.T;
            var startHdg = s.Hdg;
            var guard = 0;
            while (!step.Done(s, s.T - start, Math.Abs(Geo.AngleDiff(startHdg, s.Hdg))))
            {
                Advance(s, step, dt);
                frames.Add(ToFrame(s));
                if (++guard > fps * 600) throw new InvalidOperationException($"synthetic step {step.Name} never ends");
            }
        }
        return frames;
    }

    private static double Toward(double x, double target, double maxStep) => x + Math.Clamp(target - x, -maxStep, maxStep);

    private static void Smooth(ref double x, ref double rate, double target, double maxRate, double maxRateChange, double dt)
    {
        var distance = target - x;
        var stopRate = Math.Sqrt(2 * maxRateChange * Math.Abs(distance)) * Math.Sign(distance);
        var wanted = Math.Clamp(stopRate, -maxRate, maxRate);
        rate = Toward(rate, wanted, maxRateChange * dt);
        if (Math.Abs(distance) < 1e-9 && Math.Abs(rate) < maxRateChange * dt)
        {
            x = target;
            rate = 0;
            return;
        }
        x += rate * dt;
    }

    private static void Advance(S s, Step step, double dt)
    {
        var (gs0, hdg0, vs0) = (s.Gs, s.Hdg, s.Vs);
        s.T += dt;
        var accel = step.AccelKt * Geo.KtToMs;
        Smooth(ref s.Gs, ref s.GsRate, step.GsKt * Geo.KtToMs, accel, accel, dt);
        if (s.Gs < 0) (s.Gs, s.GsRate) = (0, 0);

        if (s.OnGround)
        {
            Smooth(ref s.Turn, ref s.TurnRateRate, step.TurnRate, 2, 2, dt);
            Smooth(ref s.Bank, ref s.BankRate, 0, 5, 5, dt);
            Smooth(ref s.Pitch, ref s.PitchRate, step.Liftoff ? step.PitchDeg : 0, 3, 3, dt);
            if (step.Liftoff && s.Kt >= 145 && s.Pitch >= 7) s.OnGround = false;
        }
        else
        {
            Smooth(ref s.Bank, ref s.BankRate, step.BankDeg, 5, 5, dt);
            s.Turn = s.Gs > 1 ? G * Math.Tan(s.Bank * Math.PI / 180) / s.Gs * 180 / Math.PI : 0;
            Smooth(ref s.Pitch, ref s.PitchRate, step.PitchDeg, 3, 3, dt);
            var vsTarget = step.VsFpm * Geo.FtToM / 60;
            if (step.Name == "flare") vsTarget = -Math.Min(-vsTarget, 0.02 + 0.3 * Math.Max(0, s.Agl * Geo.FtToM));
            Smooth(ref s.Vs, ref s.VsRate, vsTarget, 3, 2, dt);
        }

        s.Hdg = Geo.Wrap360(s.Hdg + s.Turn * dt);
        double Rad(double d) => d * Math.PI / 180;
        var vN = (gs0 * Math.Cos(Rad(hdg0)) + s.Gs * Math.Cos(Rad(s.Hdg))) / 2;
        var vE = (gs0 * Math.Sin(Rad(hdg0)) + s.Gs * Math.Sin(Rad(s.Hdg))) / 2;
        var h = s.AltFt * Geo.FtToM;
        var (m, n) = Geo.Radii(s.Lat);
        var midLat = s.Lat + vN * dt / 2 / (m + h) * 180 / Math.PI;
        s.Lat += vN * dt / (m + h) * 180 / Math.PI;
        s.Lon += vE * dt / ((n + h) * Math.Cos(Rad(midLat))) * 180 / Math.PI;
        if (!s.OnGround)
        {
            s.AltFt += (vs0 + s.Vs) / 2 * dt / Geo.FtToM;
            if (s.AltFt <= GroundFt && s.Vs < 0)
            {
                s.AltFt = GroundFt;
                (s.Vs, s.VsRate) = (0, 0);
                s.OnGround = true;
            }
        }
        else (s.Vs, s.VsRate) = (0, 0);
    }

    private static PhysicsFrame ToFrame(S s)
    {
        var hdg = s.Hdg * Math.PI / 180;
        var vN = s.Gs * Math.Cos(hdg);
        var vE = s.Gs * Math.Sin(hdg);
        return new PhysicsFrame
        {
            T = s.T,
            Lat = s.Lat,
            Lon = s.Lon,
            AltFt = s.AltFt,
            PitchDeg = -s.Pitch,
            BankDeg = -s.Bank,
            HeadingDeg = s.Hdg,
            VelWorldX = vE / Geo.FtToM,
            VelWorldY = s.Vs / Geo.FtToM,
            VelWorldZ = vN / Geo.FtToM,
            VelBodyZ = Math.Sqrt(s.Gs * s.Gs + s.Vs * s.Vs) / Geo.FtToM,
            OnGround = s.OnGround,
            GroundSpeedKt = s.Kt,
            Elevator = Math.Clamp(-s.PitchRate / 3, -1, 1),
            Aileron = Math.Clamp(s.BankRate / 5, -1, 1),
            Rudder = s.OnGround ? Math.Clamp(s.Turn / 3, -1, 1) : 0,
            GForce = 1 / Math.Cos(s.Bank * Math.PI / 180),
        };
    }
}
