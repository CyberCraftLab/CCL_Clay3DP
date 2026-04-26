; ============================================================
;  CCL-ALTAR-01 - Stoneflower M4 reverse-direction test
;  CyberCraft Lab, OTH Regensburg
;  Issue #19 prerequisite (extruder-off travel mode)
; ============================================================
;  Purpose:  confirm whether KRC4 -> Stoneflower wiring
;            routes M4 to a CCW direction signal so the
;            ram + auger physically reverse during M4.
;
;  Setup:    Robot parked in a safe pose. No motion occurs
;            in this program. Build plate clear.
;
;            First run DRY (no clay loaded) - watch the
;            ram and auger motor shafts directly to confirm
;            direction reversal visually. Once direction is
;            confirmed, you can repeat with clay loaded to
;            see pressure response and any retract behavior.
;
;  S=200 sits in the operating range per CCL-ALTAR-01
;  calibration (RPM ~= 92, about 1.5 rot/sec) - visible
;  motion without being violent.
; ============================================================

G90                  ; absolute coordinates

; --- Phase 1: forward extrusion, 3 sec ---
;     Establish a known good "forward" reference.
M3 S200
G4 X3                ; KUKA CNC: G4 X<seconds>

; --- Phase 2: stop, 2 sec ---
M5
G4 X2

; --- Phase 3: REVERSE via M4 - THIS IS THE TEST, 3 sec ---
;     Watch the motor shafts. They should reverse.
;     If they do not, KRC4 is not routing direction and
;     #19 will need to ship without reverse-retraction.
M4 S200
G4 X3

; --- Phase 4: stop, 2 sec ---
M5
G4 X2

; --- Phase 5: forward again with current cell convention, 2 sec ---
;     Confirms post-test the system still extrudes forward
;     normally (i.e. M4 didn't latch any persistent state).
M3 S200
G4 X2
S1                   ; S1 = off in the cell convention
G4 X2

M5                   ; final stop
M30                  ; program end
