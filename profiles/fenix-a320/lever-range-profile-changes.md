# 0.6.2 – thrust lever range (profile changes)

New profile hash: `sha256:170a31413a9be70cfcb4858b0c5fa88e2c329779313e11e95dddab2841811335`

Made with `tools/profile-gen` from `overrides.yaml` and the hand-written part of `profile.yaml`; the
generated block was not edited by hand. Proof that only the intended vars changed:
`dotnet run --project tools/profile-gen -- diff <profile at b4fa561> profiles/fenix-a320/profile.yaml`.

```
added 0, removed 0, changed 2
~ A_FC_THROTTLE_LEFT_INPUT: tolerance 0.2 -> 0.1; range - -> [0,5]
~ A_FC_THROTTLE_RIGHT_INPUT: tolerance 0.2 -> 0.1; range - -> [0,5]
profile hash: sha256:170a31413a9be70cfcb4858b0c5fa88e2c329779313e11e95dddab2841811335
```

The diff tool compares sync fields only. Notes changed on the same four entries: the INPUT vars list the detents,
and the GATE vars say they are never written. The other generated files changed only in `draft-report.md`
(the evidence count table).

| Change | Var | Why |
|---|---|---|
| `range: [0, 5]`, tolerance 0.2 → 0.1, detents in the note | `A_FC_THROTTLE_LEFT_INPUT`, `A_FC_THROTTLE_RIGHT_INPUT` | Read-only throttle probe 2026-11-05 (`throttle-results.md`): MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5 on both levers, no noise. Owner confirmed the range and the 0.1 takeover tolerance (2026-11-05). The follower now writes them (0.6.2). |
| note only (stays `ignore`) | `A_FC_THROTTLE_LEFT_GATE`, `A_FC_THROTTLE_RIGHT_GATE` | Fenix-computed detent value (10, 1, 8, 3, 4, 5), not the lever. Owner decision 2026-11-05: never written. `ignore` vars are never writable; `RealProfileTests` checks that the guard refuses them. |

Both pilots need this profile (same hash) for a session.
