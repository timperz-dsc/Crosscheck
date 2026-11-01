// Read-only SimConnect client for the safety check: on ground, parking brake, ground speed,
// engine N1 and combustion, and the aircraft TITLE. It defines data and requests data, and
// does nothing else: no writes, no events.

using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace ProfileProbe;

sealed class SimvarReader : ISimvarReader
{
    enum Def { Safety = 1, Title = 2 }
    enum Req { Safety = 1, Title = 2 }

    static readonly (string Name, string Unit)[] Fields =
    [
        ("SIM ON GROUND", "bool"),
        ("BRAKE PARKING POSITION", "bool"),
        ("GROUND VELOCITY", "knots"),
        ("TURB ENG N1:1", "percent"),
        ("TURB ENG N1:2", "percent"),
        ("GENERAL ENG COMBUSTION:1", "bool"),
        ("GENERAL ENG COMBUSTION:2", "bool"),
        ("L:A320_Sound:ENG_1_N1", "number"),
        ("L:A320_Sound:ENG_2_N1", "number"),
    ];

    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    Thread? _pump;
    volatile SimvarSnapshot? _latest;
    volatile string? _title;

    public SimvarSnapshot? Latest => _latest;
    public string? Title => _title;

    public Task StartAsync(CancellationToken ct)
    {
        _pump = new Thread(Pump) { IsBackground = true, Name = "SimConnect safety" };
        _pump.Start();
        return Task.CompletedTask;
    }

    void Pump()
    {
        SC? sc = null;
        while (!_stop)
        {
            try
            {
                sc ??= Connect();
                if (sc is null) { Thread.Sleep(2000); continue; }
                _wake.WaitOne(50);
                sc.ReceiveMessage();
            }
            catch (COMException)
            {
                try { sc?.Dispose(); } catch (COMException) { }
                sc = null;
                _latest = null;
            }
        }
        sc?.Dispose();
    }

    SC? Connect()
    {
        SC sc;
        try { sc = new SC("Crosscheck Profile Probe (read-only)", IntPtr.Zero, 0, _wake, 0); }
        catch (COMException) { return null; }
        foreach (var (name, unit) in Fields)
            sc.AddToDataDefinition(Def.Safety, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<SafetyData>(Def.Safety);
        sc.AddToDataDefinition(Def.Title, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0, SC.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<TitleData>(Def.Title);
        sc.OnRecvSimobjectData += (_, data) =>
        {
            if (data.dwRequestID == (uint)Req.Safety)
            {
                var d = (SafetyData)data.dwData[0];
                _latest = new SimvarSnapshot(DateTime.UtcNow, d.OnGround, d.ParkingBrake, d.GroundSpeed, d.N1a, d.N1b, d.Comb1, d.Comb2, d.SoundN1a, d.SoundN1b);
            }
            else if (data.dwRequestID == (uint)Req.Title)
                _title = ((TitleData)data.dwData[0]).Title;
        };
        // Every 6th sim frame (~5-10 Hz), always sent, so "no data" means the link is gone.
        sc.RequestDataOnSimObject(Req.Safety, Def.Safety, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 6, 0);
        sc.RequestDataOnSimObject(Req.Title, Def.Title, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        return sc;
    }

    public ValueTask DisposeAsync()
    {
        _stop = true;
        _wake.Set();
        _pump?.Join(TimeSpan.FromSeconds(2));
        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SafetyData
    {
        public double OnGround, ParkingBrake, GroundSpeed, N1a, N1b, Comb1, Comb2, SoundN1a, SoundN1b;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct TitleData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
    }
}
