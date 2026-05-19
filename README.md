# CCL_Clay3DP — Rhino 8 Plugin for Clay 3D Printing

**Version: Alpha 1.2.0 — NOT a release candidate. Use at your own risk.**

See [CHANGELOG.md](CHANGELOG.md) for what's new in this release.

> 🚧 This plugin is in **alpha**. It is actively used in the CCL lab but is
> not feature-frozen, has not been independently audited, and is known to
> have rough edges. Generated robot programs MUST be reviewed in RoboDK
> simulation before running on real hardware. The authors assume no
> liability for damage to robots, equipment, parts, or persons arising
> from the use of this software (see the Apache-2.0 disclaimer of warranty
> in [LICENSE](LICENSE) §7 and limitation of liability in §8).

A Rhino 8 plugin that takes a 3D part in Rhino, slices it into a spiral
toolpath, checks its printability for clay, and sends it straight to RoboDK
as a robot machining project — ready to run on a Kuka KR 10 R1100-2 with a
Stoneflower clay extruder.

**One input, one output.** Pick a Rhino geometry, press a button, get a
robot program. No intermediate STL / G-code files, no Cura, no manual
configuration in RoboDK.

> ## ⚠ About this project
>
> **CCL_Clay3DP was developed at the CyberCraft Lab (CCL),
> Ostbayerische Technische Hochschule (OTH) Regensburg, by
> Prof. Christophe Barlieb, for use with the lab's specific hardware
> setup (the CCL-ALTAR-01 cell).**
>
> It is therefore tightly coupled to that setup — named RoboDK frames
> (`T10`, `T11`, `T12`, `BasePlate02`), a specific Kuka robot model,
> a custom KUKA CNC ISG post processor, and the Stoneflower paste
> extruder workflow. **It is not a general-purpose clay slicer.**
>
> The source is released publicly under the Apache License 2.0 so
> that **other labs, workshops, and practitioners are welcome to fork
> it and adapt it to their own hardware**. If you have a different
> robot, TCP naming, post processor, or extruder, expect to rewrite
> the bits of `RoboDK/RoboDKSubprocess.cs` and
> `Models/RobotSettings.cs` that hard-code CCL's station topology —
> the core spiral slicer, printability analyzer, and plugin
> scaffolding should all carry over unchanged.
>
> Contributions, forks, and questions are welcome. Credit back to
> the CyberCraft Lab and Prof. Barlieb is appreciated but not
> legally required by the license.

## What it does

1. **Toolpath generator** — two modes, chosen in Settings:
   - **Spiral Slice** — continuous spiral between horizontal contours
     (vase mode, no Z-seam).
   - **Layer Slice** — discrete planar layer contours. With the optional
     **Outer Wall Bracing** flag, each layer also gets a bracing rib
     anchored to an inward-offset projection (the projection itself is
     not baked or printed, only the outer wall and the bracing). The
     bracing pattern is either a sharp triangle-wave **zigzag** (default)
     or a smooth **cosine wave** (`Sinusoidal bracing` toggle); both
     share the same contact-point count and "french kiss" half-bead
     overlap with the outer-wall bead at each touch point. The number of
     contact points is set independently of the toolpath sampling density
     (`Bracing contact points`, Issue #11). Robot print order per layer is
     **Outer Toolpath → Bracing Toolpath**, then up to the next layer.
     Outer Wall Bracing only runs on ruled / extrudable geometry
     (cylinders, prisms, cones, planar extrusions); the algorithm
     refuses to run on free-form / organic Breps.
2. **Skirt — always on.** Every part begins with a 15 mm outward
   skirt loop printed before the part itself, derived from the lowest
   contour. Primes the extruder, gives a visible alignment reference,
   and closes the loop without lifting before the part starts.
3. **Auto-translation to origin.** The selected geometry is moved so
   its bbox bottom-center sits on world origin (commercial-slicer
   convention) — model + toolpath + skirt + build-volume preview all
   share the same frame of reference. Move is undoable with Ctrl+Z.
4. **Printability analysis** — colors the geometry mesh in the viewport
   with a red-yellow-green heatmap showing where clay printing is at risk
   (overhang angles, layer bond, robot wrist velocity). Works in every
   mode; three channels (Clay, Robot, Both) all render to the mesh.
5. **Preview Clay Model** — renders whichever toolpath curves exist
   (`Spiral Toolpath` / `Outer Toolpath` / `Bracing Toolpath` / `Skirt`)
   as mesh bead tubes at the configured Clay bead diameter, with
   mesh-sphere joints at every vertex for continuity, plus a PBR
   clay material per preset (Porcelain / Stoneware / Earthenware) for
   realistic Rendered-viewport output.
6. **Build Volume preview.** A wireframe box on layer
   `3DP::Build Volume` shows the cell's printable space (X/Y bounds
   and Z height configurable in Settings; CCL-ALTAR-01 default is
   400 × 400 × 1000 mm centered on origin).
7. **RoboDK integration** — opens the pre-configured `3DP_v0.4.rdk`
   station, adds the toolpath (skirt + part as one continuous curve)
   as a Curve Follow object under the build plate, links it to the
   `3DP_Template` machining project, and regenerates the robot
   program using the CYARC KUKA post processor.

**Workflow gating.** The Settings form is the first thing the user must
interact with; all other workflow buttons (Slice, Analyze, Send, Preview Clay Model)
stay disabled until Settings has been reviewed once in the session.
Switching between Spiral and Layer modes automatically clears the other
mode's generated layers on the next Slice, so the Rhino Layers panel
always reflects only the current mode. RoboDK never launches
automatically — Send to RoboDK (or Run All) is the only thing that
opens / re-sends to it.

**Settings file Import / Export.** The Settings dialog has `Import…`
and `Export…` buttons for round-tripping the entire configuration
(Clay, Toolpath, Robot, Build Volume) as a JSON file. Useful for
versioning lab configurations and sharing between machines.

## Repository layout

```
3DP/                          Repository root
├─ CCL_Clay3DP/               C# Rhino 8 plugin source (the main product)
├─ PostProcessor/             KUKA CNC ISG post processor (copy into RoboDK)
├─ robodk_station/            RoboDK station template (3DP_v0.4.rdk)
│                                + bundled RoboDK app config (settings.ini,
│                                layout6.0.1.ini) for matching the CCL UI
├─ 3DP.sln                    Visual Studio solution
├─ LICENSE                    Apache License 2.0
├─ NOTICE                     Third-party attributions
└─ README.md                  This file
```

## Plugin structure

```
CCL_Clay3DP/
├─ CCL_Clay3DP.csproj           .NET Framework 4.8 build config
├─ CCL_Clay3DPPluginInfo.cs     Plugin + panel registration
├─ CC-logo/                     CyberCraft signet logo (embedded resource)
├─ G-Code_Proofing/             Reference .nc output used to validate posts
├─ Commands/
│  └─ CCL_Clay3DPCommand.cs     Rhino command that toggles the panel
├─ UI/
│  ├─ CCL_Clay3DPPanel.cs       Dockable Eto.Forms panel
│  └─ PluginIcon.cs              Loads the CC logo as the panel-tab icon
├─ Settings/
│  ├─ SettingsDialog.cs          Eto.Forms settings window
│  └─ SettingsManager.cs         JSON persistence to %APPDATA% (with legacy migration)
├─ Models/
│  ├─ ClayMaterialSettings.cs    Bead diameter, overhang, density
│  ├─ ClayPresets.cs             Porcelain / Stoneware / Earthenware
│  ├─ RobotSettings.cs           Feed/travel speed, spindle, tilt, nozzle tool
│  ├─ Parameters.cs              Helix + height + build-volume + selection objects
│  └─ PipelineSettings.cs        Top-level settings aggregate
├─ Core/
│  ├─ ContourSlicer.cs           Horizontal-plane contour extraction
│  ├─ SpiralInterpolator.cs      Seam-aligned spiral between contours
│  ├─ FrameComputer.cs           Tool frames (outward surface normal)
│  ├─ SkirtBuilder.cs            Lowest-contour outward 15 mm skirt loop +
│  │                              uniform-arclength frame sampler with +Z normal
│  ├─ GeometryCurvature.cs       Ruled-surface gate for Outer Wall Bracing
│  ├─ GeometrySelector.cs        Brep/Mesh picker
│  └─ ThinwallSpiralResult.cs    Container for slicer output (incl. skirt)
├─ Analysis/
│  ├─ PrintabilityAnalyzer.cs    Per-frame score (overhang, bond, curv, wrist)
│  ├─ PrintabilityResult.cs      Score container + issue report
│  └─ HeatmapDisplay.cs          Vertex-colored mesh overlay in viewport
├─ Zigzag/
│  └─ ZigzagGenerator.cs         Outer Wall Bracing generators: triangle-wave
│                                weave (BuildSingleContour) or smooth cosine
│                                (BuildSinusoidalSingleContour). Both anchor
│                                to an inward in-plane projection, apply a
│                                half-bead kiss offset (Issue #11), and the
│                                zigzag adds hairpin trim for concave geometry
└─ RoboDK/
   ├─ FrameSerializer.cs         Frames + settings → temp JSON
   └─ RoboDKSubprocess.cs        Generates & runs Python 3 script via RoboDK's
                                 embedded interpreter
```

### Rhino layers produced

All output lives under a shared `3DP` parent layer. Only the layers that
the current slice mode needs are created; switching modes (or changing
settings) removes the others on the next Slice.

| Layer | Content | Visible by default |
|---|---|---|
| `3DP::Spiral Toolpath` | The spiral curve (Spiral mode) | yes |
| `3DP::Outer Toolpath` | Outer wall curve per layer (Layer mode) | yes |
| `3DP::Bracing Toolpath` | Zigzag or sinusoidal rib per layer (Layer + Bracing) | yes |
| `3DP::Bracing Vectors` | Inward-direction arrows (Layer + Bracing) | no (flip preview) |
| `3DP::Bracing Outer Points` | Sample points on outer wall (Layer + Bracing) | no |
| `3DP::Bracing Inner Points` | Sample points on inward projection (Layer + Bracing) | no |
| `3DP::Skirt` | Outward-offset skirt loop, 15 mm from lowest contour (always) | yes |
| `3DP::Build Volume` | Wireframe box of the printable cell envelope (always) | yes |
| `3DP::Print Position` | RGB axis cross at world origin (always) | yes |
| `3DP::Clay Model` | Mesh-bead visualization (any mode, Preview Clay Model button) | yes |
| `3DP::Heatmap` | Vertex-colored input mesh (Analyze) | yes |

In Layer + Bracing mode, the **Outer Toolpath** layer always carries
the slice contour (the geometrically outer wall) regardless of flip
direction. The "Flip inward direction" prompt swaps which side the
bracing rib extends to (inward by default, outward when flipped); the
inward-projected curve itself is computed as the bracing's anchor but
is neither baked nor printed.

## Prerequisites

- **Rhino 8** (uses Eto.Forms + RhinoCommon 8.x)
- **.NET Framework 4.8** for building the plugin
- **RoboDK** with the Python API (`C:\RoboDK\Python`) installed
- **Kuka KR 10 R1100-2 station** with the following items:
  - Robot named `KUKA KR 10 R1100-2`
  - Tool named `BasePlate02` (the build plate on the robot)
  - Reference frames named `T10`, `T11`, `T12` (nozzle TCPs)
  - A pre-configured `3DP_Template` machining project
- The bundled **KUKA CNC ISG post processor** installed into RoboDK
  (see below)

## Post processor installation

The plugin drives a custom KUKA CNC 2.1 ISG post processor that
handles `#HSC` BSPLINE smoothing, the `T<N> M6` tool change derived
from the reference frame, and extruder lead compensation.

The post processor lives in this repository at:

```
PostProcessor/KUKA_CNC_2_1_ISG_CCL_3DP_WIP_MS_S_INT_HSC_WAIT_S_DELAY.py
```

**You must copy it into RoboDK's `Posts` folder** for RoboDK to find
it. On a standard Windows install that is:

```
C:\RoboDK\Posts\KUKA_CNC_2_1_ISG_CCL_3DP_WIP_MS_S_INT_HSC_WAIT_S_DELAY.py
```

If RoboDK is installed somewhere else, drop the file into the
equivalent `Posts\` folder inside your RoboDK installation
directory. The filename must match exactly — the plugin references
it by name at runtime.

After copying, in RoboDK you can verify the post is recognised via:
**Program → Add/Edit Post Processor → Select Post → pick
`KUKA_CNC_2_1_ISG_CCL_3DP_WIP_MS_S_INT_HSC_WAIT_S_DELAY`**. The
plugin's `RoboDKSubprocess.cs` assigns it automatically on the robot
each time *Send to RoboDK* runs, so no manual selection is needed
during normal operation.

The bundled post is a modification of RoboDK Inc.'s original KUKA
CNC 2.1 ISG Kernel post (Apache-2.0). CCL modifications are listed
in the file header and in [NOTICE](NOTICE).

## RoboDK app config (recommended for the same UI as CCL)

For the most consistent experience, the repository ships RoboDK's
own UI configuration files alongside the station so you get the same
view, panel layout, and view-on-load behavior we use at the CCL lab.

The relevant files live at:

```
robodk_station/settings.ini          general RoboDK preferences
                                     (incl. AUTO_FIT_NEW=false so the
                                     view saved in the station is the
                                     view you get on every reopen)
robodk_station/layout6.0.1.ini       window / dock panel layout for
                                     RoboDK 6.0.x
```

**To install:**

1. **Close RoboDK fully.** RoboDK rewrites these files on exit, so a
   running instance will clobber whatever you copy in.
2. Copy both files into your RoboDK config directory:

   ```
   %APPDATA%\RoboDK\
   ```

   (typically `C:\Users\<you>\AppData\Roaming\RoboDK\`).
3. Reopen RoboDK and load `robodk_station/3DP_v0.4.rdk`. You should
   see the same docked panels and saved camera framing we use.

The layout file is RoboDK-version-specific (`layout6.0.1.ini` →
RoboDK 6.0.x). On a different RoboDK version the file is ignored
harmlessly — no risk to your install — but you won't get the panel
layout match. Update from a current CCL machine if you need the
layout for a newer RoboDK build.

These files are optional. The plugin works without them; copying
them in just gives you the same UX we have at CCL.

## Building

```bash
cd CCL_Clay3DP
dotnet build
```

The output DLL lands in `bin\Debug\CCL_Clay3DP.dll`. Load it in
Rhino via `_PlugInManager → Install…`.

## Using the plugin

1. Open Rhino, type `CCL_Clay3DP` to show the dockable panel.
2. Click **Settings** to configure:
   - **Clay material**: preset (Porcelain / Stoneware / Earthenware) or
     custom bead diameter, max overhang, bond ratio, density.
   - **Toolpath**: Spiral Slice (vase mode) or Layer Slice; Outer Wall
     Bracing (Layer mode only, ruled-geometry only); Bracing contact
     points (4–500, independent of Frame spacing); Sinusoidal bracing
     (smooth cosine path vs. zigzag); Spiral follows curve normal (Spiral
     mode only); layer height; frame spacing in mm (uniform arc-length
     between toolpath frames — Issue #22); CCW/CW direction.
   - **Robot / printer**: feed rate (mm/s), travel speed, spindle S
     value, nozzle tool (T10/T11/T12), RoboDK paths.
   - **Build Volume (mm)**: X min/max, Y min/max, Z height of the cell
     envelope (defaults match CCL-ALTAR-01).
   - `Import…` / `Export…` round-trip the whole config to a JSON file.
3. Click **1. Slice** — pick a Brep/Mesh in Rhino. The plugin moves
   the picked geometry to origin (undoable with `Ctrl+Z`), then
   produces:
   - The toolpath layer (spiral or per-layer outer / bracing).
   - The skirt on `3DP::Skirt` (always, 15 mm outward of lowest
     contour).
   - The build-volume box on `3DP::Build Volume`.
   - The Print Position RGB cross on `3DP::Print Position`.
4. Click **2. Analyze** — choose the heatmap channel (Clay, Robot, Both).
   The geometry mesh is recolored on `3DP::Heatmap`.
5. Click **3. Send to RoboDK** — opens RoboDK, loads the station
   template, adds the curve (skirt + part as one continuous path)
   under `BasePlate02`, binds it to `3DP_Template`, regenerates the
   program. All speeds, spindle, and reference frame come from the
   plugin settings. RoboDK only opens / re-sends when this button (or
   *Run All*) is clicked — never automatically.

## RoboDK station expectations

The `3DP_v0.4.rdk` station must contain a machining project named
`3DP_Template` that the plugin will populate. On first use, configure it
once:

- Robot = `KUKA KR 10 R1100-2`
- Tool = `BasePlate02`
- Reference = one of `T10` / `T11` / `T12` (the plugin overrides this per run)
- Algorithm = `Robot holds object`
- `Aproach/Retract each curve` unchecked (the plugin enforces this anyway)
- Post processor set to the custom CYARC KUKA CNC post

Save the station. From then on, the plugin manages everything
programmatically via the `Machining` and `ProgEvents` param dicts.

## Settings propagated from plugin to RoboDK

| Plugin setting | RoboDK parameter | Effect |
|---|---|---|
| `FeedRate` | `SpeedOperation` | Operation speed along the curve |
| `TravelSpeed` | `SpeedRapid` | Approach / retract speed |
| `SpindleSpeed` | `CallPathStart` event | `S<value>` inserted before first curve point |
| `NozzleTool` | `PoseFrame` on template | T10 / T11 / T12 frame becomes the reference |
| (always 0) | `ApproachRetractAll` | Disables per-curve approach/retract |
| (always `S1`) | `CallPathFinish` event | Extruder off after last curve point |
| (always 0) | `Rounding` / `RoundingOn` | No blending (KUKA CNC handles via `#HSC`) |

## Troubleshooting

- **Rhino crashes when opening Settings** → make sure the `FileFilter`
  API call uses separate `name` and `ext` arguments (not the WinForms
  pipe-delimited string).
- **Plugin won't load after rebuild** → Rhino locks the DLL; close Rhino
  before `dotnet build`.
- **`3DP_Template` machining project not found** → create it once in the
  station as described above and save the `.rdk` file.
- **T10/T11/T12 not applied** → the template's pose frame is set via
  `setPoseFrame` at run time; ensure the frame names in the station match
  exactly.

## License

**Apache License 2.0**

Copyright 2026 CyberCraft Lab, OTH Regensburg, Prof. Christophe Barlieb

Licensed under the Apache License, Version 2.0 (the "License"); you may
not use this software except in compliance with the License. You may
obtain a copy of the License in the [LICENSE](LICENSE) file at the root
of this repository, or at <http://www.apache.org/licenses/LICENSE-2.0>.

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
implied. See the License for the specific language governing
permissions and limitations under the License.

Third-party attributions (RhinoCommon, Newtonsoft.Json, Eto.Forms,
RoboDK API) are documented in [NOTICE](NOTICE).

## Credits

Developed by the **CyberCraft Lab (CCL)**, Ostbayerische Technische
Hochschule (OTH) Regensburg, under **Prof. Christophe Barlieb**,
founder of the CyberCraft Lab and co-founder of the CyberCraft
Kolleg — who led the design of the print workflow, the spiral-slice
strategy, and the integration with the CCL-ALTAR-01 cell.

The plugin is tuned for the **CCL-ALTAR-01** setup:

- KUKA KR 10 R1100-2 robot arm
- Fixed Stoneflower paste/clay extruder (nozzle TCPs `T10` / `T11` / `T12`)
- Robot-held build plate (`BasePlate02`)
- KUKA CNC ISG 2.1 controller
- Custom post processor with HSC BSPLINE smoothing and extruder
  lead compensation

### Adapting to your own lab

If you want to use this plugin with different hardware, the pieces
most likely to need rewiring are:

| File | What's lab-specific |
|---|---|
| `RoboDK/RoboDKSubprocess.cs` | Robot / tool / frame names, post processor path, the `3DP_Template` machining project assumption |
| `Models/RobotSettings.cs` | Default paths to RoboDK, default station template, nozzle tool list |
| `Settings/SettingsDialog.cs` | Nozzle dropdown options (`T10`/`T11`/`T12`) |
| Your copy of the KUKA post processor | G-code style, tool-change command syntax, spindle command mapping |

The **slicer** (`Core/`), the **printability analyzer**
(`Analysis/`), and the plugin scaffolding (`UI/`, `Settings/`,
`Commands/`) are generic and should run on any Rhino 8 install
without changes.

Feedback, bug reports, and pull requests are welcome via the public
repository. Please keep the Apache 2.0 license, the LICENSE/NOTICE
files, and the attribution headers intact in any derivative work.

## Development note

This plugin was developed iteratively with the help of Anthropic's
**Claude** (Opus 4.7) acting as a pair-programming assistant, under
the direction of Prof. Christophe Barlieb at the CyberCraft Lab. The
domain expertise (hardware setup, KUKA CNC post processor tuning,
Stoneflower extruder calibration, and the overall print workflow) is
the lab's; the AI assisted with C# scaffolding, refactoring, Eto.Forms
panel plumbing, and boilerplate generation. Commits where Claude
contributed significantly are tagged with `Co-Authored-By:
Claude Opus 4.7` trailers in the git log.

As with any AI-assisted code, we recommend reviewing critical paths
(especially the RoboDK and post-processor interaction in
`RoboDK/RoboDKSubprocess.cs`) before using the plugin on production
hardware.

## Acknowledgments

Special thanks to **Peter Kinader of Sematek GmbH**, our system
integrator, whose work commissioning the KUKA CNC ISG controller
and the CCL-ALTAR-01 cell made this plugin possible. His
contributions to the custom post processor and the extruder lead
compensation logic are foundational to the print quality the plugin
can deliver.

Thanks to the team who contributed to the **CyberCraft Lab
technologies** — the hardware, robotic cell, extrusion system, and
supporting software that this plugin drives:

- **Prof. Florian Weininger**
- **Volker Lindner**
- **Marc Schmailzl**
- **Sebastian Voigt**
- **Luis Maurer**

Thanks to those who contributed to the **construction of the lab
and its infrastructure**:

- **Martin Foster**
- **Andreas Besenhard**
- **Alois Bräu**
- **Christina Eichinger**

And thanks for the **administrative and financial support** that
kept the project running:

- **Dean Andreas Emminger**
- **Prof. Thomas Linner**

Thanks also to the **Faculty of Architecture at OTH Regensburg**
for providing the academic home, studio space, and material support
that allowed the CyberCraft Lab to develop and test this work.

This research was made possible through funding and institutional
support from:

- **Bayerisches Staatsministerium für Wissenschaft und Kunst**
  (Bavarian State Ministry of Science and the Arts)
- **bidt — Bayerisches Forschungsinstitut für Digitale
  Transformation** (Bavarian Research Institute for Digital
  Transformation, part of the Bavarian Academy of Sciences)
- **OTH Regensburg** (Ostbayerische Technische Hochschule
  Regensburg)
