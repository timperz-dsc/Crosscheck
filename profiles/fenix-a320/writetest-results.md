# Supervised write test, 2026-11-03 19:54


== flaps lever (S_FC_FLAPS)
   starts at 0; values this test may write: 0, 1 (scan 0; positions runs 2026-11-03: 0 … 4)
   0 -> 1 (lever to 1): STUCK for 2.5 s, read back after 25 ms
     watched: S_FC_FLAPS_LATCH 0 -> 0; FLAPS_INTERNAL_CONFIG 0 -> 0
     also changed: N_STANDBY_ASI
   1 -> 0 (lever back to 0): STUCK for 2.5 s, read back after 18 ms
     watched: S_FC_FLAPS_LATCH 0 -> 0; FLAPS_INTERNAL_CONFIG 0 -> 0
     also changed: I_EXT_DOOR_L1_CABPR, I_EXT_DOOR_L2_CABPR, I_EXT_DOOR_L3_CABPR, I_EXT_DOOR_L4_CABPR, I_EXT_DOOR_R1_CABPR, I_EXT_DOOR_R2_CABPR, I_EXT_DOOR_R3_CABPR, I_EXT_DOOR_R4_CABPR
   back at 0 (where it started)

== speed brake lever (A_FC_SPEEDBRAKE)
   starts at 1; values this test may write: 0, 1, 2.0234375 (scan 1 (RET); gate test 2: 2.0234375 sent twice; positions runs 2026-11-03: 0 = ARM)
   1 -> 2.0234375 (half): STUCK for 2.5 s, read back after 23 ms
     watched: B_FC_SPEEDBRAKE 0 -> 1
     also changed: I_ECAM_CLR_LEFT, I_ECAM_CLR_RIGHT, I_MIP_MASTER_CAUTION_CAPT, I_MIP_MASTER_CAUTION_CAPT_L, I_MIP_MASTER_CAUTION_FO, I_MIP_MASTER_CAUTION_FO_L, N_STANDBY_ASI
   2.0234375 -> 1 (RET): STUCK for 2.5 s, read back after 27 ms
     watched: B_FC_SPEEDBRAKE 1 -> 0
     also changed: I_ECAM_CLR_LEFT, I_ECAM_CLR_RIGHT, I_MIP_MASTER_CAUTION_CAPT, I_MIP_MASTER_CAUTION_CAPT_L, I_MIP_MASTER_CAUTION_FO, I_MIP_MASTER_CAUTION_FO_L, N_STANDBY_ASI
   1 -> 0 (ARM): STUCK for 2.5 s, read back after 27 ms
     watched: B_FC_SPEEDBRAKE 0 -> 0
     also changed: I_EXT_DOOR_L1_CABPR, I_EXT_DOOR_L2_CABPR, I_EXT_DOOR_L3_CABPR, I_EXT_DOOR_L4_CABPR, I_EXT_DOOR_R1_CABPR, I_EXT_DOOR_R2_CABPR, I_EXT_DOOR_R3_CABPR, I_EXT_DOOR_R4_CABPR
   0 -> 1 (RET): STUCK for 2.5 s, read back after 18 ms
     watched: B_FC_SPEEDBRAKE 0 -> 0
     also changed: I_EXT_DOOR_L1_CABPR, I_EXT_DOOR_L2_CABPR, I_EXT_DOOR_L3_CABPR, I_EXT_DOOR_L4_CABPR, I_EXT_DOOR_R1_CABPR, I_EXT_DOOR_R2_CABPR, I_EXT_DOOR_R3_CABPR, I_EXT_DOOR_R4_CABPR
   back at 1 (where it started)

== A/SKID & N/W STRG switch (S_FC_MIP_ANTI_SKID)
   starts at 1; values this test may write: 0, 1 (scan 1; positions runs 2026-11-03: 1 -> 0 -> 1)
   1 -> 0 (OFF): STUCK for 2.5 s, read back after 17 ms
     also changed: I_ECAM_CLR_LEFT, I_ECAM_CLR_RIGHT, I_MIP_MASTER_CAUTION_CAPT, I_MIP_MASTER_CAUTION_CAPT_L, I_MIP_MASTER_CAUTION_FO, I_MIP_MASTER_CAUTION_FO_L, N_STANDBY_ASI
   0 -> 1 (ON): STUCK for 2.5 s, read back after 31 ms
     also changed: I_ECAM_CLR_LEFT, I_ECAM_CLR_RIGHT, I_MIP_MASTER_CAUTION_CAPT, I_MIP_MASTER_CAUTION_CAPT_L, I_MIP_MASTER_CAUTION_FO, I_MIP_MASTER_CAUTION_FO_L, N_STANDBY_ASI
   back at 1 (where it started)

== cockpit door (S_COCKPIT_DOOR)
   skipped: it rests at 0, not at a start this test accepts; put it back by hand and run --only DOOR

== FCU SPD knob (S_FCU_SPEED)
   starts at 0; values this test may write: -1, 0, 1 (positions runs: gate test 2 prep -1 (push), 2026-11-03 0 -> 1 -> 0 with the managed lamp and dashes)
   0 -> 1 (own + 1 (pull: selected speed)): STUCK for 2.5 s, read back after 31 ms
     watched: I_FCU_SPEED_MANAGED 1 -> 0; B_FCU_SPEED_DASHED 1 -> 0; N_FCU_SPEED 136 -> 136; I_FCU_SPEED_MODE 1 -> 1
     expected I_FCU_SPEED_MANAGED 0, B_FCU_SPEED_DASHED 0: yes
     also changed: I_EXT_DOOR_L1_CABPR, I_EXT_DOOR_L2_CABPR, I_EXT_DOOR_L3_CABPR, I_EXT_DOOR_L4_CABPR, I_EXT_DOOR_R1_CABPR, I_EXT_DOOR_R2_CABPR, I_EXT_DOOR_R3_CABPR, I_EXT_DOOR_R4_CABPR
   1 -> 0 (own - 1 (push: managed speed)): STUCK for 2.5 s, read back after 21 ms
     watched: I_FCU_SPEED_MANAGED 0 -> 1; B_FCU_SPEED_DASHED 0 -> 1; N_FCU_SPEED 136 -> 136; I_FCU_SPEED_MODE 1 -> 1
     expected I_FCU_SPEED_MANAGED 1, B_FCU_SPEED_DASHED 1: yes
     also changed: N_STANDBY_ASI
   back at 0 (where it started)

all controls done

# Supervised write test, 2026-11-03 19:56


== flaps lever (S_FC_FLAPS)
   starts at 0; values this test may write: 0, 1 (scan 0; positions runs 2026-11-03: 0 … 4)
   0 -> 1 (lever to 1): STUCK for 2.5 s, read back after 22 ms
     watched: S_FC_FLAPS_LATCH 0 -> 0; FLAPS_INTERNAL_CONFIG 0 -> 0
     also changed: N_STANDBY_ASI

# Supervised write test, 2026-11-03 19:56


== cockpit door (S_COCKPIT_DOOR)
   starts at 1; values this test may write: 0, 1 (scan 1 (closed); positions runs 2026-11-03: 1 -> 0 -> 1 with I_PED_COCKPIT_DOOR_U)
   1 -> 0 (open): STUCK for 2.5 s, read back after 42 ms
     watched: I_PED_COCKPIT_DOOR_U 1 -> 0; I_PED_COCKPIT_DOOR_L 0 -> 0; B_DOORS_COCKPIT_LOCKED 0 -> 0
     expected I_PED_COCKPIT_DOOR_U 0: yes
     also changed: I_OH_DOOR_CTL_U, N_STANDBY_ASI
   0 -> 1 (closed): STUCK for 2.5 s, read back after 27 ms
     watched: I_PED_COCKPIT_DOOR_U 0 -> 1; I_PED_COCKPIT_DOOR_L 0 -> 0; B_DOORS_COCKPIT_LOCKED 0 -> 0
     expected I_PED_COCKPIT_DOOR_U 1: yes
     also changed: I_OH_DOOR_CTL_U, N_STANDBY_ASI, N_STANDBY_DDRMI_COMPASS
   back at 1 (where it started)

all controls done

# Supervised write test, 2026-11-04 17:58

bridge write path (--bridge-profile): 70 SimConnect definitions (62 read chunks for 1960 watched vars + 8 write slots), as in a bridge session


== captain's tray table (S_TRAY_TABLE_CAPT)
   starts at 0; values this test may write: 0, 1 (scan 0; positions run 2026-11-03: 0, 1; gate test 3: refused past the definition limit; cosmetic, nothing in the aircraft depends on it)
   0 -> 1 (position 1): STUCK for 2.5 s, read back after 23 ms
   1 -> 0 (position 0): STUCK for 2.5 s, read back after 25 ms
   back at 0 (where it started)

all controls done

