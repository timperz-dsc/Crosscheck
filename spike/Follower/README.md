# spike/Follower (CRO-17)

Can a follower aircraft be held where the PF's aircraft is, every frame? Record a circuit,
play it back into your own sim as the follower through a simulated network, and compare
placement methods by numbers. **Offline only, your own sim only.**

How to use it, the safety rules and what is measured:
[docs/spikes/CRO-17-follower.md](../../docs/spikes/CRO-17-follower.md).

| Project | What |
|---|---|
| `Follower.Core` | everything: file format, playout buffer, interpolation, network simulation, metrics, report, synthetic circuit, safety (cross-platform) |
| `Follower.Core.Tests` | unit and offline end-to-end tests, plus tripwires on the source (run anywhere) |
| `Follower.Cli` | `follower`: synth, simulate, inspect, compare (no sim needed) |
| `Follower.Sim` | `follower-sim`: record, play, restore (Windows, SimConnect) |

```
dotnet build spike/Follower/Follower.sln
dotnet test spike/Follower/Follower.Core.Tests
```

Not part of the bridge, and the bridge never references it.
