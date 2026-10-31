// SimConnect side of the scanner: talks to the WASM module through two client data areas and can
// read individual L: vars for spot checks. Everything here only reads L: vars.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.FlightSimulator.SimConnect;

record LVar(int Id, string Name, double Value);

sealed class Scanner : IDisposable
{
    const int CmdSize = 256;
    const int DataSize = 8000;
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    public string SimVersion { get; private set; } = "unknown";

    readonly SimConnect sc;
    readonly AutoResetEvent wake = new(false);
    readonly ConcurrentQueue<Action<SimConnect>> work = new();
    readonly BlockingCollection<string> replies = new();
    readonly ConcurrentDictionary<int, double> spotValues = new();
    readonly Thread pump;
    volatile bool stop;
    uint seq;
    int spotDefs = (int)Def.FirstSpot;

    public Scanner()
    {
        sc = new SimConnect("Crosscheck LVar Scanner client", IntPtr.Zero, 0, wake, 0);
        sc.OnRecvOpen += (_, d) => SimVersion =
            $"{d.dwApplicationVersionMajor}.{d.dwApplicationVersionMinor}.{d.dwApplicationBuildMajor}.{d.dwApplicationBuildMinor}";
        sc.OnRecvException += (_, d) => Console.Error.WriteLine($"SimConnect exception {(SIMCONNECT_EXCEPTION)d.dwException} (sendID {d.dwSendID})");
        sc.OnRecvClientData += (_, d) =>
        {
            if (d.dwRequestID != (uint)Req.Data) return;
            var bytes = ((DataBlock)d.dwData[0]).Bytes;
            int end = Array.IndexOf(bytes, (byte)0);
            replies.Add(Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end));
        };
        sc.OnRecvSimobjectData += (_, d) => spotValues[(int)d.dwRequestID] = ((OneVar)d.dwData[0]).Value;

        sc.MapClientDataNameToID("CROSSCHECK.LVARSCAN.CMD", Area.Cmd);
        sc.MapClientDataNameToID("CROSSCHECK.LVARSCAN.DATA", Area.Data);
        sc.AddToClientDataDefinition(Def.Cmd, 0, CmdSize, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.AddToClientDataDefinition(Def.Data, 0, DataSize, 0, SimConnect.SIMCONNECT_UNUSED);
        sc.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, DataBlock>(Def.Data);
        sc.RequestClientData(Area.Data, Req.Data, Def.Data, SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
            SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

        pump = new Thread(Pump) { IsBackground = true, Name = "SimConnect" };
        pump.Start();
    }

    /// <summary>Asks the module for its version and L: var count; null if it does not answer.</summary>
    public (string Version, int Count)? Ping()
    {
        uint s = ++seq;
        Send($"PING {s}");
        var reply = WaitFor($"PONG {s} ");
        if (reply == null) return null;
        var parts = reply.Trim().Split(' ');
        return (parts[2], int.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    /// <summary>One consistent snapshot of all L: vars, as taken by the module in a single frame.</summary>
    public List<LVar> Scan()
    {
        uint s = ++seq;
        Send($"SCAN {s}");
        var result = new List<LVar>();
        int expected = -1;
        for (int chunk = 0; ; chunk++)
        {
            var reply = WaitFor($"CHUNK {s} {chunk} ") ?? throw new TimeoutException($"No chunk {chunk} from the module.");
            int newline = reply.IndexOf('\n');
            var header = reply[..newline].Split(' '); // CHUNK seq index total count
            int total = int.Parse(header[3], CultureInfo.InvariantCulture);
            expected = int.Parse(header[4], CultureInfo.InvariantCulture);
            foreach (var line in reply[(newline + 1)..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                result.Add(new LVar(int.Parse(f[0], CultureInfo.InvariantCulture), f[1], double.Parse(f[2], CultureInfo.InvariantCulture)));
            }
            if (chunk + 1 >= total) break;
            Send($"ACK {s} {chunk}");
        }
        if (result.Count != expected) throw new InvalidDataException($"Module reported {expected} vars but sent {result.Count}.");
        return result;
    }

    /// <summary>Reads L: vars directly through SimConnect data definitions (for spot checks).</summary>
    public Dictionary<string, double> ReadDirect(IReadOnlyList<string> names)
    {
        var ids = new int[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            int id = spotDefs++;
            ids[i] = id;
            var name = names[i];
            Post(s =>
            {
                s.AddToDataDefinition((Def)id, "L:" + name, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                s.RegisterDataDefineStruct<OneVar>((Def)id);
                s.RequestDataOnSimObject((Req)id, (Def)id, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            });
        }
        var until = DateTime.UtcNow + ReplyTimeout;
        while (ids.Any(id => !spotValues.ContainsKey(id)) && DateTime.UtcNow < until) Thread.Sleep(20);
        return Enumerable.Range(0, names.Count).Where(i => spotValues.ContainsKey(ids[i])).ToDictionary(i => names[i], i => spotValues[ids[i]]);
    }

    void Send(string command)
    {
        var bytes = new byte[CmdSize];
        Encoding.ASCII.GetBytes(command, 0, command.Length, bytes, 0);
        Post(s => s.SetClientData(Area.Cmd, Def.Cmd, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new CmdBlock { Bytes = bytes }));
    }

    string? WaitFor(string prefix)
    {
        var until = DateTime.UtcNow + ReplyTimeout;
        while (DateTime.UtcNow < until)
        {
            if (replies.TryTake(out var reply, 100) && reply.StartsWith(prefix)) return reply;
        }
        return null;
    }

    void Post(Action<SimConnect> action)
    {
        work.Enqueue(action);
        wake.Set();
    }

    void Pump()
    {
        while (!stop)
        {
            wake.WaitOne(5);
            while (work.TryDequeue(out var action)) action(sc);
            try { sc.ReceiveMessage(); }
            catch (COMException) { stop = true; }
        }
    }

    public void Dispose()
    {
        stop = true;
        wake.Set();
        pump.Join();
        sc.Dispose();
    }

    enum Area { Cmd, Data }
    enum Def { Cmd, Data, FirstSpot = 100 }
    enum Req { Data = 0 }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct CmdBlock
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = CmdSize)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct DataBlock
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DataSize)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OneVar
    {
        public double Value;
    }
}
