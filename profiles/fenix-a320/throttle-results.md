# Fenix A320 thrust lever range (read-only probe)

Run 2026-11-05 18:12:15, aircraft title `FenixA320 IAE WF`, `profile-probe throttle` (flight-1 diagnosis item 7).
Read only: nothing was written to the sim. Setup checked before every step: on the ground, parking brake set,
ENG MASTERs off, no engine running, external power.

About 54 frames per second; longest gap between two frames 60 ms.

## Noise (hands off, levers at IDLE)

548 frames over 9991 ms.

| Var | n | min | max | mean | median | stddev |
|---|---|---|---|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 548 | 2 | 2 | 2 | 2 | 0 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 548 | 2 | 2 | 2 | 2 | 0 |
| `A_FC_THROTTLE_LEFT_GATE` | 548 | 8 | 8 | 8 | 8 | 0 |
| `A_FC_THROTTLE_RIGHT_GATE` | 548 | 8 | 8 | 8 | 8 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 548 | 0 | 0 | 0 | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 548 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle1Position` | 548 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle2Position` | 548 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 548 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 548 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 548 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 548 | 0 | 0 | 0 | 0 | 0 |

Plateau band (4 × the largest input stddev, at least its spread, at least 0.005): **0.005**.

## Detents (both levers held, 2 s each)

| Detent | left INPUT median | right INPUT median | right − left | left GATE | right GATE | left spread | right spread |
|---|---|---|---|---|---|---|---|
| MAX REV | 0 | 0 | 0 | 10 | 10 | 0 | 0 |
| REV IDLE | 1 | 1 | 0 | 1 | 1 | 0 | 0 |
| IDLE | 2 | 2 | 0 | 8 | 8 | 0 | 0 |
| CL | 3 | 3 | 0 | 3 | 3 | 0 | 0 |
| FLX/MCT | 4 | 4 | 0 | 4 | 4 | 0 | 0 |
| TOGA | 5 | 5 | 0 | 5 | 5 | 0 | 0 |

Left and right agree within the noise band (0.005) at every recorded detent.

Every lever var per detent:

### MAX REV (110 frames)

| Var | n | min | max | mean | median | stddev |
|---|---|---|---|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A_FC_THROTTLE_LEFT_GATE` | 110 | 10 | 10 | 10 | 10 | 0 |
| `A_FC_THROTTLE_RIGHT_GATE` | 110 | 10 | 10 | 10 | 10 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 110 | 0 | 0 | 0 | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 110 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle1Position` | 110 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle2Position` | 110 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 110 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 110 | 0 | 0 | 0 | 0 | 0 |

### REV IDLE (108 frames)

| Var | n | min | max | mean | median | stddev |
|---|---|---|---|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 108 | 1 | 1 | 1 | 1 | 0 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 108 | 1 | 1 | 1 | 1 | 0 |
| `A_FC_THROTTLE_LEFT_GATE` | 108 | 1 | 1 | 1 | 1 | 0 |
| `A_FC_THROTTLE_RIGHT_GATE` | 108 | 1 | 1 | 1 | 1 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 108 | 0 | 0 | 0 | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 108 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle1Position` | 108 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle2Position` | 108 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 108 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 108 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 108 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 108 | 0 | 0 | 0 | 0 | 0 |

### IDLE (110 frames)

| Var | n | min | max | mean | median | stddev |
|---|---|---|---|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 110 | 2 | 2 | 2 | 2 | 0 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 110 | 2 | 2 | 2 | 2 | 0 |
| `A_FC_THROTTLE_LEFT_GATE` | 110 | 8 | 8 | 8 | 8 | 0 |
| `A_FC_THROTTLE_RIGHT_GATE` | 110 | 8 | 8 | 8 | 8 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 110 | 0 | 0 | 0 | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 110 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle1Position` | 110 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle2Position` | 110 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 110 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 110 | 0 | 0 | 0 | 0 | 0 |

### CL (110 frames)

| Var | n | min | max | mean | median | stddev |
|---|---|---|---|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 110 | 3 | 3 | 3 | 3 | 0 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 110 | 3 | 3 | 3 | 3 | 0 |
| `A_FC_THROTTLE_LEFT_GATE` | 110 | 3 | 3 | 3 | 3 | 0 |
| `A_FC_THROTTLE_RIGHT_GATE` | 110 | 3 | 3 | 3 | 3 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 110 | 0 | 0 | 0 | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 110 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle1Position` | 110 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle2Position` | 110 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 110 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 110 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 110 | 0 | 0 | 0 | 0 | 0 |

### FLX/MCT (105 frames)

| Var | n | min | max | mean | median | stddev |
|---|---|---|---|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 105 | 4 | 4 | 4 | 4 | 0 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 105 | 4 | 4 | 4 | 4 | 0 |
| `A_FC_THROTTLE_LEFT_GATE` | 105 | 4 | 4 | 4 | 4 | 0 |
| `A_FC_THROTTLE_RIGHT_GATE` | 105 | 4 | 4 | 4 | 4 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 105 | 0 | 0 | 0 | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 105 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle1Position` | 105 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle2Position` | 105 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 105 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 105 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 105 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 105 | 0 | 0 | 0 | 0 | 0 |

### TOGA (103 frames)

| Var | n | min | max | mean | median | stddev |
|---|---|---|---|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 103 | 5 | 5 | 5 | 5 | 0 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 103 | 5 | 5 | 5 | 5 | 0 |
| `A_FC_THROTTLE_LEFT_GATE` | 103 | 5 | 5 | 5 | 5 | 0 |
| `A_FC_THROTTLE_RIGHT_GATE` | 103 | 5 | 5 | 5 | 5 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 103 | 0 | 0 | 0 | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 103 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle1Position` | 103 | 0 | 0 | 0 | 0 | 0 |
| `XMLVAR_Throttle2Position` | 103 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 103 | 0 | 0 | 0 | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 103 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 103 | 0 | 0 | 0 | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 103 | 0 | 0 | 0 | 0 | 0 |

## Full range (all holds and sweeps)

| Var | min | max |
|---|---|---|
| `A_FC_THROTTLE_LEFT_INPUT` | 0 | 5 |
| `A_FC_THROTTLE_RIGHT_INPUT` | 0 | 5 |
| `A_FC_THROTTLE_LEFT_GATE` | 0 | 10 |
| `A_FC_THROTTLE_RIGHT_GATE` | 0 | 10 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_1` | 0 | 0 |
| `WT_VIRTUAL_THROTTLE_LEVER_POS_2` | 0 | 0 |
| `XMLVAR_Throttle1Position` | 0 | 0 |
| `XMLVAR_Throttle2Position` | 0 | 0 |
| `S_FC_THROTTLE_LEFT_MAX_REVERSE` | 0 | 0 |
| `S_FC_THROTTLE_RIGHT_MAX_REVERSE` | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:1` | 0 | 0 |
| `A:GENERAL ENG THROTTLE LEVER POSITION:2` | 0 | 0 |

## Sweeps (one lever, MAX REV → TOGA)

### left lever

815 frames over 15087 ms; `A_FC_THROTTLE_LEFT_INPUT` 0 … 5; largest change between two frames 0.3337; the other lever (`A_FC_THROTTLE_RIGHT_INPUT`) moved 0.

Plateaus of at least 250 ms within the band 0.005 (a detent shows as a plateau even on a steady sweep):

| from ms | to ms | frames | median | min | max | matches |
|---|---|---|---|---|---|---|
| 0 | 1461 | 80 | 0 | 0 | 0 | MAX REV (held 0) |
| 2791 | 3409 | 34 | 1 | 0.9971 | 1 | REV IDLE (held 1) |
| 3603 | 4155 | 32 | 2 | 1.9973 | 2 | IDLE (held 2) |
| 7636 | 8360 | 41 | 3 | 2.997 | 3 | CL (held 3) |
| 9341 | 10251 | 51 | 4 | 3.9979 | 4 | FLX/MCT (held 4) |
| 11602 | 15087 | 192 | 5 | 4.9962 | 5 | TOGA (held 5) |

### right lever

1130 frames over 20827 ms; `A_FC_THROTTLE_RIGHT_INPUT` 0 … 5; largest change between two frames 0.0462; the other lever (`A_FC_THROTTLE_LEFT_INPUT`) moved 0.

Plateaus of at least 250 ms within the band 0.005 (a detent shows as a plateau even on a steady sweep):

| from ms | to ms | frames | median | min | max | matches |
|---|---|---|---|---|---|---|
| 0 | 1677 | 93 | 0 | 0 | 0.0018 | MAX REV (held 0) |
| 3194 | 4002 | 45 | 1 | 0.9974 | 1 | REV IDLE (held 1) |
| 4634 | 5609 | 53 | 2 | 1.9961 | 2 | IDLE (held 2) |
| 10524 | 11989 | 81 | 3 | 2.9959 | 3 | CL (held 3) |
| 14065 | 16145 | 114 | 4 | 3.9972 | 4 | FLX/MCT (held 4) |
| 19346 | 20827 | 82 | 5 | 4.9978 | 5 | TOGA (held 5) |

## Proposal for overrides.yaml (not applied: to be confirmed by the owner)

Range = lowest and highest INPUT value of both levers over every hold and sweep, rounded outward to 0.01. Detents = mean of the left and right hold medians. Noise band 0.005.

```yaml
  - name: A_FC_THROTTLE_LEFT_INPUT
    class: stream
    tolerance: 0.2
    range: [0, 5]
    note: "thrust lever: the PF streams it (CRO-13); detents MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5"
    evidence: "read-only throttle probe 2026-11-05: holds MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5; full travel 0 … 5 (both levers, holds and sweeps); noise band 0.005; GATE MAX REV 10, REV IDLE 1, IDLE 8, CL 3, FLX/MCT 4, TOGA 5; owner confirmation pending"
  - name: A_FC_THROTTLE_RIGHT_INPUT
    class: stream
    tolerance: 0.2
    range: [0, 5]
    note: "thrust lever: the PF streams it (CRO-13); detents MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5"
    evidence: "read-only throttle probe 2026-11-05: holds MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5; full travel 0 … 5 (both levers, holds and sweeps); noise band 0.005; GATE MAX REV 10, REV IDLE 1, IDLE 8, CL 3, FLX/MCT 4, TOGA 5; owner confirmation pending"
```
