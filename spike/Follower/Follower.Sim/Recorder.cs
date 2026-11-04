// Records the user aircraft at sim-frame rate into a .cxrec file. Read-only: its own SimConnect
// client that only defines and requests data. It writes nothing to the sim and sends no events
// (a test checks this file for any write or event call).

using System.Diagnostics;
using System.Runtime.InteropServices;
using Crosscheck.Follower;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace Crosscheck.Follower.Sim;

sealed class Recorder(string path, string note)
{
    public int Run(StopSignal stop)
    {
        var wake = new AutoResetEvent(false);
        SC? sc = null;
        Console.WriteLine("connecting to the sim (read-only) ...");
        while (sc is null && !stop.IsSet)
        {
            sc = SimLink.TryConnect("Crosscheck Follower Recorder (read-only)", wake);
            if (sc is null) stop.Wait(TimeSpan.FromSeconds(2));
        }
        if (sc is null) return 1;

        var clock = Stopwatch.StartNew();
        string? title = null;
        RecordingWriter? writer = null;
        var early = new List<Frame>(); // frames before the aircraft title arrived (at most 2 s)
        Frame last = default;
        var quit = false;
        try
        {
            sc.OnRecvQuit += (_, _) => quit = true;
            sc.OnRecvException += (_, d) => Console.WriteLine($"SimConnect exception {(SIMCONNECT_EXCEPTION)d.dwException} (index {d.dwIndex})");
            sc.OnRecvSimobjectData += (_, data) =>
            {
                if (data.dwRequestID == (uint)Req.Title)
                {
                    title = ((TitleData)data.dwData[0]).Title;
                    return;
                }
                if (data.dwRequestID != (uint)Req.State) return;
                var frame = SimLink.ToFrame(clock.Elapsed.TotalSeconds, (StateData)data.dwData[0]);
                if (writer is null)
                {
                    early.Add(frame);
                    if (title is null && frame.T - early[0].T < 2) return;
                    writer = new RecordingWriter(File.Create(path), new RecordingHeader(DateTime.UtcNow, title ?? "unknown", note));
                    foreach (var f in early) writer.Append(f);
                    early.Clear();
                }
                else writer.Append(frame);
                last = frame;
                if (writer.Count % 300 == 1)
                    Console.Write($"\r{writer.Count,7} frames  {frame.T,6:0} s  {(frame.OnGround ? "ground" : "air   ")}  {frame.GroundSpeedKt,5:0} kt  {frame.AltFt,6:0} ft   ");
            };
            SimLink.RequestState(sc);
            Console.WriteLine($"recording to {path}. Press S or Esc (or Ctrl+C) to stop.");
            while (!stop.IsSet && !quit)
            {
                wake.WaitOne(20);
                sc.ReceiveMessage();
            }
        }
        catch (COMException e)
        {
            Console.WriteLine($"\nsim connection lost: {e.Message}");
        }
        finally
        {
            writer?.Dispose();
            try { sc.Dispose(); } catch (COMException) { }
        }

        Console.WriteLine();
        if (writer is null)
        {
            Console.WriteLine("no frames received; nothing written");
            return 1;
        }
        Console.WriteLine($"wrote {path}: {writer.Count} frames, {last.T:0} s, aircraft \"{title ?? "unknown"}\"");
        if (!last.OnGround || last.GroundSpeedKt > EndCheck.StoppedKt)
            Console.WriteLine("warning: the recording does not end stopped on the ground. The player refuses it unless --allow-air-end is given; record a full circuit to a stop.");
        return 0;
    }
}
