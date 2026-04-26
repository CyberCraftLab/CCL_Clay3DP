# BenchTests

Self-contained KUKA CNC G-code programs for verifying cell behavior
**outside** the CCL_Clay3DP slicer pipeline. Each file is a small,
focused experiment — useful when investigating a hardware or wiring
question that the slicer-generated output can't easily isolate.

These programs are **not** invoked by the plugin. Run them manually
on the controller when you need to characterize the cell.

## Conventions

- All programs assume **CCL-ALTAR-01** (KUKA KR 10 R1100-2,
  KRC4 + KUKA CNC 2.1 ISG kernel, Stoneflower BIG ram + auger).
- Spindle conventions per the cell:
  `M3 S<v>` = forward CW, `M5` = stop, `S1` = off.
  `M4 S<v>` is the open question for several of these tests.
- Dwell uses `G4 X<seconds>` (KUKA convention), not `G4 P<ms>`.
- Programs are **motion-free** unless explicitly noted in the header.
  Always run with the build plate clear and the robot in a safe pose.

## Tests

| File | What it checks | Issue |
|---|---|---|
| `m4_reverse_test.nc` | Whether `M4` actually reverses the Stoneflower ram + auger (KRC4 direction-pin wiring). | #19 prerequisite |
