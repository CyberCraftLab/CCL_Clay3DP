# Changelog

All notable changes to **CCL_Clay3DP** are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning is [Semantic](https://semver.org/) with an `-alpha` suffix
while the plugin is pre-release.

## [1.1.1-alpha] — 2026-04-25

Polish release rolling up GitLab issues #1, #2, #4, #5, #6, #7, #8, #9
plus a critical Outer Wall Bracing fix and several side-of-mouth
quality-of-life additions. Issue #3 (Deconstruct) is the only
original-list item still pending and will land in a later release.

### Added

- **Skirt** (Issue #8). Every part now starts with a 15 mm outward
  skirt loop printed before the part itself — primes the extruder and
  gives a visible alignment reference. Always-on (no setting), single
  closed loop derived from the lowest contour. Visualized in Rhino on
  the `3DP::Skirt` layer (blue) and concatenated in front of the part
  toolpath in the curve sent to RoboDK, so the robot prints
  *skirt → part* with one approach at the start and one retract at the
  end (no Z lift between the two).
- **Build Volume preview** (companion to Issue #8). A dark-cyan
  wireframe box visualizes the cell's printable space at slice time,
  on layer `3DP::Build Volume`. Bounds are user-editable in Settings
  (X min/max, Y min/max, Z height — Z always starts at the build
  plate). Defaults match CCL-ALTAR-01: −200 to 200 mm in X and Y,
  1000 mm tall.
- **Print Position marker** (Issue #1). A small RGB axis cross at
  world origin marks where the print physically lands, on layer
  `3DP::Print Position`.
- **Auto-translate to origin** (Issue #1). Selecting a Brep/Mesh and
  clicking Slice now physically moves the source object so the bbox
  bottom-center sits on world origin — the commercial-slicer
  convention. Move is undoable with `Ctrl+Z`. Subsequent slices on
  the same object are no-ops. Status messages report the distance
  moved (e.g. *"Geometry translated 31.8 mm to origin for printing"*).
- **Import / Export Settings file** (Issue #9). Settings dialog
  gains `Import…` and `Export…` buttons. Export writes the current
  dialog values to a JSON file without touching the global config;
  Import loads a JSON into the dialog (pending until OK). Lets users
  share / version-control their tuned configurations.
- **Bundled CCL RoboDK app config** (Issue #4). The repo now ships
  `robodk_station/settings.ini` and `robodk_station/layout6.0.1.ini`
  alongside the `.rdk` station. Copying these into `%APPDATA%\RoboDK\`
  matches the lab's RoboDK UI layout, enables `AUTO_FIT_NEW=false`
  (so saved views in the station persist across reopens), and uses
  the same panel/dock arrangement we work with at CCL. README has
  install instructions.
- **PBR clay material per preset** (Issue #5 / #6). Preview Clay
  Model now applies a physically-based render material to the
  `3DP::Clay Model` layer that matches the selected clay preset
  (Porcelain / Stoneware / Earthenware) — including correct
  Roughness and Subsurface for Porcelain. Renders properly in Rhino
  8's Rendered viewport.
- **Spiral follows curve normal** (Toolpath group). Optional banking
  of the build plate to follow the surface normal of the part during
  Spiral mode. Off by default; comes with a custom warning dialog
  when enabled.
- **RoboDK out-of-sync warnings**. When a settings change clears the
  Rhino toolpath but RoboDK still holds the previous send, a
  yellow-icon "RoboDK out of sync" modal fires so the user doesn't
  accidentally simulate stale program state.
- **Outer Wall Bracing — ruled-geometry gate**. Bracing now refuses
  to run on free-form / organic geometry (sphere, sweep, organic
  Brep) with a clear modal directing the user to either disable
  bracing or pick an extruded / ruled-surface part (cylinder, prism,
  cone, planar extrusion). The per-layer DivideByCount + seam-align
  algorithm only behaves on ruled geometry; on free-form shapes the
  seam wanders between layers and the bracing twists. Mesh inputs
  are accepted (no NURBS degree to test, trust the user).

### Changed

- **"Inner Wall Bracing" renamed to "Outer Wall Bracing"** (Issue
  #7). Reinterpreted: the slice contour is *always* the outer wall
  regardless of flip direction. Inner toolpath is no longer baked
  and no longer printed — the inner curve only exists as the
  bracing's inward anchor. Settings checkbox label and `HelixParameters`
  field renamed accordingly.
- **Settings dialog scrollable** (Issue #2). Settings groups now
  live in a scrollable container so smaller laptop displays don't
  push the OK / Cancel row off-screen. Trimmed redundant fields
  (Height offset bottom/top, Travel speed, Ribbon width); retired
  the ribbon-mesh visualization entirely.
- **Settings dialog adds Build Volume group**. Five fields for the
  cell envelope (X min, X max, Y min, Y max, Z height). Editable,
  exported / imported via the Settings file plumbing.
- **No auto-launch of RoboDK on settings change**. RoboDK no longer
  launches or re-sends automatically when the user changes settings
  — that is now reserved for explicit `Send to RoboDK` (and
  `Run All`) clicks. The settings-change handler still re-bakes the
  Build Volume box and Print Position marker so the visual stays
  fresh, and warns if the previous RoboDK send is now stale.
- **`Pipes` button renamed to `Preview Clay Model`** (Issue #5).
  Clarifies the button's actual function. Sphere stride was added
  to bound mesh density on long polylines.

### Fixed

- **Outer Wall Bracing on free-form geometry** (parked critical
  issue closed by the gate above). Previously, bracing on organic
  shapes produced a twisted unprintable structure; now those inputs
  are rejected up front instead of silently producing junk.
- **RoboDK Curve Follow binding** ("Path points: 0", "Invalid
  input" popup, red-X icon). Caused by `setMachiningParameters`
  being called with the `'No_Update'` flag — that mode generated
  valid programs but never registered the UI binding. Now the
  Machining + ProgEvents `setParam` calls run *first*, then
  `setMachiningParameters` is called *without* `'No_Update'`,
  binding the curve into the project's UI dropdown and triggering
  the path solve in one step.
- **Tolerant template lookup**. If the saved `.rdk` station is in a
  renamed-template state (because the user accidentally hit Save in
  RoboDK after a previous run), the script now finds the template
  by project-name prefix instead of crashing with `RuntimeError:
  3DP_Template machining project not found`.
- **Stale-cleanup that doesn't nuke its own template**. When the
  saved-state's renamed name happens to equal the new run's
  computed name, the old cleanup path would delete the chosen
  template. Now compares by RoboDK's internal `.item` handle and
  skips the template itself.
- **Build Volume box and Print Position marker survive settings
  changes**. Previously wiped by `ClearGeneratedContent` and never
  re-baked until the next manual Slice; now the settings handler
  re-bakes both regardless of slice mode.
- **Geometry origin translation no longer destroys companion
  geometry references**. Translates the actual selected object
  (commercial-slicer convention) rather than translating a hidden
  copy, removing the previous "where did my model go?" surprise.
- **`.gitignore` tidy** baseline (commit 5d2d2fa, on `main`).

### Removed

- **`3DP::Ribbon` layer** and the ribbon-mesh visualization. The
  feature was never used and complicated the Settings dialog.
- **`3DP::Inner Toolpath` layer** in Outer Wall Bracing mode. The
  inner curve still computes (as the bracing's inward anchor) but
  is no longer baked or printed.
- **Travel speed UI field, Height offset bottom/top fields, Ribbon
  width field** from the Settings dialog (Issue #2 trim).

### Migration notes

- Users on **v1.1.0-alpha**: no action needed for plugin
  installation; just rebuild and reload. Persisted settings will be
  read; new fields (Build Volume) come from defaults on first load.
- If you'd previously hit `Save` in RoboDK on the loaded station,
  your `.rdk` may carry a renamed `3DP_Template`. The plugin now
  handles this transparently, but to clean up: open the `.rdk`,
  rename any `3DP_<...>` machining project back to `3DP_Template`,
  delete generated programs, and resave.

[1.1.1-alpha]: https://gitlab.oth-regensburg.de/bac39641/ccl_clay3dp/-/tags/v1.1.1-alpha

---

## [1.1.0-alpha] — earlier

Initial alpha tag rolled out before the polish branch. Carried the
Spiral / Layer slice modes, the Stoneflower-tuned printability
analyzer, the RoboDK Send pipeline (single curve, single approach +
retract), and the original Inner Wall Bracing scaffolding (since
renamed to Outer Wall Bracing). See branch history for details.
