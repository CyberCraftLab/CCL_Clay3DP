# CCL_Clay3DP — Architecture overview

Generated 2026-05-03 on branch `settings-ui` (Slices 1, 2a, 2b, 2d shipped; Slice 2e in progress).

This document is meant to be printed and pinned next to the workstation. Each diagram is a Mermaid block — render in VS Code (Mermaid extension), GitHub/GitLab, or paste into <https://mermaid.live> to view/export PNG.

---

## 1. Folder map

```
CCL_Clay3DP/
├── UI/
│   ├── CCL_Clay3DPPanel.cs        # The dockable panel — every user-visible button lives here
│   └── ...
├── Settings/
│   ├── SettingsDialog.cs          # Modal dialog with Material / Tool / Toolpath / Robot / Build Volume
│   └── SettingsManager.cs         # JSON load/save (settings.json); also Import/Export to user file
├── Models/                         # Pure data classes — no behavior, no dependencies on other layers
│   ├── PipelineSettings.cs        # Root settings record (composes the others)
│   ├── ClayMaterialSettings.cs    # Bead diameter, max overhang, density, water %, shrinkage comp
│   ├── BaseSettings.cs            # Multi-layer raft (Issue #10)
│   ├── RobotSettings.cs           # Feed rate, spindle, nozzle, RoboDK paths
│   ├── Parameters.cs              # GeometrySelection, BuildVolumeSettings, HelixParameters, HeightParameters
│   └── ClayPresets.cs             # Porcelain / Stoneware / Earthenware presets
├── Core/                           # Geometry pipeline primitives
│   ├── GeometrySelector.cs        # Rhino object picker (Brep/Surface/Mesh)
│   ├── GeometryCurvature.cs       # IsRuled() — bracing-compatibility check
│   ├── ContourSlicer.cs           # Brep/Mesh → planar contour curves at layer Z's
│   ├── SpiralInterpolator.cs      # Contours → spiral toolpath points
│   ├── FrameComputer.cs           # Toolpath points → robot Plane frames (with normals)
│   ├── SkirtBuilder.cs            # Bottom contour + offset → skirt curve + frames
│   └── ThinwallSpiralResult.cs    # SpiralResult — the cached output of one slice
├── Analysis/
│   ├── PrintabilityAnalyzer.cs    # Per-frame scoring (overhang, bond, curvature, taper, wrist)
│   ├── PrintabilityResult.cs      # FrameScore, AnalysisChannel enum, PrintabilityResult
│   ├── HeatmapDisplay.cs          # Visualizes per-frame scores on the geometry mesh
│   └── BuildVolumeCheck.cs        # Slice 2d — bbox vs build volume overflow detection
├── RoboDK/
│   ├── FrameSerializer.cs         # SpiralResult.Frames → JSON for the Python script
│   └── RoboDKSubprocess.cs        # Runs the Python script via cmd.exe; Python connects to RoboDK
└── Zigzag/                         # Outer Wall Bracing (Layer Slice mode)
    └── ...
```

**Dependency direction (lower depends on higher, never the reverse):**

```mermaid
graph TD
    UI[UI/CCL_Clay3DPPanel]
    SettingsLayer[Settings/]
    AnalysisLayer[Analysis/]
    RoboDKLayer[RoboDK/]
    CoreLayer[Core/]
    ZigzagLayer[Zigzag/]
    ModelsLayer[Models/]

    UI --> SettingsLayer
    UI --> AnalysisLayer
    UI --> RoboDKLayer
    UI --> CoreLayer
    UI --> ZigzagLayer
    UI --> ModelsLayer

    SettingsLayer --> ModelsLayer
    AnalysisLayer --> CoreLayer
    AnalysisLayer --> ModelsLayer
    RoboDKLayer --> ModelsLayer
    CoreLayer --> ModelsLayer
    ZigzagLayer --> CoreLayer
    ZigzagLayer --> ModelsLayer
```

**Models has no dependencies** — it's the leaf. Anything circular here is a bug.

---

## 2. The panel — every user-visible button

```mermaid
flowchart TD
    Settings([Settings button]) --> OnSettingsClick
    Slice([1. Slice button]) --> OnSliceClick
    Analyze([2. Analyze button]) --> OnAnalyzeClick
    Send([3. Send to RoboDK button]) --> OnSendToRoboDKClick
    RunAll([Run All button]) --> OnRunAllClick
    Preview([Preview Clay Model button]) --> OnPreviewClayModelClick

    OnSettingsClick --> SettingsDialog
    SettingsDialog -->|user clicks OK| AutoRebuildPath
    AutoRebuildPath["Auto-rebuild path<br/>(Slice 2e: calls RunPipeline<br/>except Layer+Bracing)"]

    OnSliceClick --> SlicePipeline[Slice pipeline<br/>see section 3]
    OnAnalyzeClick --> HeatmapDisplay
    OnSendToRoboDKClick --> SendPipeline[Send pipeline<br/>see section 4]
    OnRunAllClick -->|chains| OnSliceClick
    OnRunAllClick -->|then| OnAnalyzeClick
    OnRunAllClick -->|then| OnSendToRoboDKClick

    OnPreviewClayModelClick --> PreviewBuild[Build mesh tube around toolpath<br/>at bead diameter]
```

**Key gates that block buttons:**

- `_settingsReviewed` — Slice/Analyze/Send/RunAll/Preview are disabled until the user opens Settings at least once per session. Avoids users running with stale defaults.
- `_lastSliceOutOfBounds` — set by post-slice build-volume check (Slice 2d). When true, Send shows a "robot would crash" popup and refuses to run. RunAll short-circuits past Analyze + Send.

---

## 3. Slice pipeline (post-Slice-2e — the target architecture)

```mermaid
flowchart TD
    SliceClick[OnSliceClick] --> Pick[GeometrySelector.Select<br/>user picks Brep/Surface/Mesh]
    Pick -->|null| Cancel1([cancelled])
    Pick --> BracingGate{Bracing + free-form?<br/>Layer mode + bracing<br/>+ NOT IsRuled}
    BracingGate -->|yes| RejectBracing([popup: bracing not permitted])
    BracingGate -->|no| Translate[ComputePrintingTranslation<br/>doc.Objects.Transform<br/>auto-translate to origin]
    Translate --> CacheRaw[_lastRawGeometry = post-translate selection<br/>used by auto-rebuild]
    CacheRaw --> RunPipelineIn[RunPipeline]

    SettingsAuto[OnSettingsClick<br/>auto-rebuild branch] --> LayerBracingCheck{Layer + Bracing?}
    LayerBracingCheck -->|yes| FallbackMsg([clear + 'click Slice<br/>to regenerate' message])
    LayerBracingCheck -->|no| RunPipelineIn

    RunPipelineIn --> Reset[_lastSliceOutOfBounds = false]
    Reset --> Clear[ClearGeneratedContent<br/>_lastResult = null]
    Clear --> Shrink{Shrinkage compensation enabled?}
    Shrink -->|yes| ApplyScale[Transform.Scale about Point3d.Origin<br/>factor = 1 / 1-pct/100]
    Shrink -->|no| Bake
    ApplyScale --> Bake[BakePrintPositionMarker<br/>BakeBuildVolume]
    Bake --> Status[SetStatus: translation + scale note]
    Status --> PreCheck[BuildVolumeCheck.SelectionBoundingBox<br/>BuildVolumeCheck.Check]
    PreCheck -->|HasOverflow| PrePopup{ConfirmPreSliceBuildVolumeOverflow}
    PrePopup -->|Cancel| Cancel2([slice cancelled])
    PrePopup -->|Continue Anyway| ModeChoice
    PreCheck -->|fits| ModeChoice
    ModeChoice{Spiral or Layer?}
    ModeChoice -->|Spiral| RunSliceAndBake[RunSliceAndBake<br/>see section 3a]
    ModeChoice -->|Layer| RunLayerSlice[RunLayerSlice]
    RunSliceAndBake --> PostCheck
    RunLayerSlice --> PostCheck
    PostCheck[BuildVolumeCheck.FrameStreamBoundingBox<br/>skirt + base + part frames]
    PostCheck -->|HasOverflow| ArmFlag[_lastSliceOutOfBounds = true<br/>NotifyPostSliceBuildVolumeOverflow popup]
    PostCheck -->|fits| Done([slice complete])
    ArmFlag --> Done2([baked but Send blocked])
```

### 3a. Spiral runner — RunSliceAndBake

```mermaid
flowchart TD
    Start[RunSliceAndBake input:<br/>scaled GeometrySelection]
    Start --> Base{Base enabled?<br/>BaseSettings.EnableBase}
    Base -->|yes| BuildBase[PrepareBaseGeometry<br/>shifts working geometry up<br/>by N x LayerHeight]
    Base -->|no| Working[working = original]
    BuildBase --> Working
    Working --> SliceContours[ContourSlicer.SliceBrep / SliceMesh<br/>at layer Z's]
    SliceContours --> Spiral[SpiralInterpolator.Interpolate<br/>contours into spiral points]
    Spiral --> Frames[FrameComputer.ComputeFrames<br/>points + outward surface normal]
    Frames --> SpiralCurve[SpiralInterpolator.CreateSpiralCurve]
    SpiralCurve --> Store[Store SpiralResult:<br/>ToolpathPoints, Frames, SpiralCurve, Contours]
    Store --> BakeCurve[BakeResults: bake SpiralCurve to layer<br/>'3DP::Spiral Toolpath']
    BakeCurve --> Skirt[BakeSkirt + sample SkirtFrames]
    Skirt --> Done([_lastResult populated])
```

`RunLayerSlice` is similar but produces per-layer closed loops instead of a continuous spiral, and may also generate Outer Wall Bracing zigzags via the Zigzag/ module.

---

## 4. Send-to-RoboDK pipeline

```mermaid
flowchart TD
    Send[OnSendToRoboDKClick]
    Send --> OOB{_lastSliceOutOfBounds?}
    OOB -->|yes| Block([popup: robot would crash<br/>Send refused])
    OOB -->|no| RunningCheck{IsRoboDKRunning?<br/>Process.GetProcessesByName}
    RunningCheck -->|yes| Confirm{ConfirmReplaceRoboDKSession<br/>YesNo popup}
    Confirm -->|No| Cancelled([cancelled])
    Confirm -->|Yes| Perform
    RunningCheck -->|no| Perform
    Perform[PerformSendToRoboDK]
    Perform --> Combine[Combine SkirtFrames + BaseFrames + Frames<br/>into one continuous stream]
    Combine --> Serialize[FrameSerializer.SerializeToFile<br/>writes JSON to temp file]
    Serialize --> Subprocess[RoboDKSubprocess.SendFrames]
    Subprocess --> WriteScript[Generate Python script with<br/>JSON path + RoboDK paths from settings]
    WriteScript --> Cmd[Process.Start cmd.exe<br/>cmd /c python script  log]
    Cmd --> Python[Python: connects to RoboDK via Robolink<br/>auto-launches RoboDK if not running]
    Python --> LoadStation[setParam Unsaved=0<br/>CloseStation<br/>AddFile station_template.rdk]
    LoadStation --> AddCurve[AddCurve<br/>combined skirt+spiral curve]
    AddCurve --> Bind[Find 3DP_Template machining item<br/>setMachiningParameters with curve]
    Bind --> Done([Program generated in RoboDK])
    Cmd --> Wait5s[C# WaitForExit 5000ms<br/>only catches fast failures]
    Wait5s -.-> Hand[Hand off to background<br/>Python keeps running]
```

**Known issue (under investigation):** on first send (cold-start RoboDK), the Python script's `Robolink()` call should auto-launch RoboDK, but the immediate `setParam`/`CloseStation`/`AddFile` calls hit the API before RoboDK is fully ready. Symptom: first send "succeeds" silently but RoboDK doesn't appear; second send works because RoboDK is already up. Fix is its own slice (RoboDK launch reliability) — needs a captured log to diagnose precisely.

---

## 5. Settings flow

```mermaid
flowchart LR
    Disk[(settings.json<br/>%APPDATA%/CCL_Clay3DP)]
    Manager[SettingsManager.Load / Save]
    Live[_settings in panel<br/>PipelineSettings instance]
    Dialog[SettingsDialog<br/>landscape 3-col layout]
    UserFile[(User-chosen JSON file)]

    Disk -->|Load on panel ctor| Manager
    Manager --> Live
    Live -->|new Dialog _settings| Dialog
    Dialog -->|OK| ApplyDialogToSettings[ApplyDialogToSettings<br/>writes every field back]
    ApplyDialogToSettings --> Manager
    Manager --> Disk

    Dialog -->|Import button| UserFile
    UserFile -->|SettingsManager.LoadFrom| Dialog
    Dialog -->|Export button| UserFile2[(User-chosen JSON file)]
```

### PipelineSettings composition

```mermaid
classDiagram
    class PipelineSettings {
        ClayMaterialSettings Clay
        HelixParameters Helix
        RobotSettings Robot
        BaseSettings Base
        HeightParameters Height
        BuildVolumeSettings BuildVolume
    }
    class ClayMaterialSettings {
        string PresetName
        double BeadDiameter
        double MaxOverhangAngle
        double MinLayerBondRatio
        double MaterialDensity
        double WaterPercent
        bool EnableShrinkageCompensation
        double ShrinkagePercent
    }
    class HelixParameters {
        double LayerHeight
        bool DirectionCCW
        int FramesPerLayer
        bool SpiralSlice
        bool OuterWallBracing
        bool SpiralFollowsCurveNormal
        double StartAngle
    }
    class RobotSettings {
        double FeedRate
        double MaxWristAngularVelocity
        double SpindleSpeed
        string NozzleTool
        string RoboDKExecutablePath
        string RoboDKStationTemplatePath
        string RoboDKProjectName
    }
    class BaseSettings {
        bool EnableBase
        int LayerCount
    }
    class BuildVolumeSettings {
        double XMin
        double XMax
        double YMin
        double YMax
        double Height
    }
    PipelineSettings --> ClayMaterialSettings
    PipelineSettings --> HelixParameters
    PipelineSettings --> RobotSettings
    PipelineSettings --> BaseSettings
    PipelineSettings --> BuildVolumeSettings
```

---

## 6. Cached state in the panel — fields that span method calls

| Field | Type | Set by | Read by | Purpose |
|---|---|---|---|---|
| `_settings` | `PipelineSettings` | ctor (`Load`), `OnSettingsClick` | every handler | live settings used by the pipeline |
| `_settingsReviewed` | `bool` | `OnSettingsClick` (first OK) | button enable gate | force user through Settings before slicing |
| `_lastResult` | `SpiralResult` | runners (`RunSliceAndBake`, `RunLayerSlice`) | `OnAnalyzeClick`, `OnSendToRoboDKClick`, post-slice check | toolpath cache for Analyze + Send |
| `_lastGeometry` | `GeometrySelection` | runners | `OnAnalyzeClick` (heatmap), `OnSettingsClick` (marker re-bake) | scaled selection used by heatmap display |
| `_lastRawGeometry` | `GeometrySelection` | `OnSliceClick` (post-translate, pre-shrinkage) | `OnSettingsClick` auto-rebuild | input for full-pipeline regenerate after settings change (Slice 2e) |
| `_lastSliceOutOfBounds` | `bool` | post-slice check (Slice 2d) | `OnSendToRoboDKClick` (block), `OnRunAllClick` (short-circuit) | gate to prevent robot crash |
| `_lastTranslationNote` | `string` | OnSliceClick / RunPipeline | `WithTranslationNote` (consumed once) | stash translation+scale message so completion status can append it |
| `_hasSentToRoboDK` | `bool` | `PerformSendToRoboDK` (on success) | `OnSettingsClick` (warn-stale trigger) | tracks whether RoboDK is in the workflow this session |
| `_gatedControls` | `List<Control>` | ctor (button list) | `OnSettingsClick` (unlock on first review) | controls disabled until `_settingsReviewed` |

---

## 7. Where to look when fixing X

| Symptom | First file to open |
|---|---|
| Settings dialog field added/changed | `Settings/SettingsDialog.cs` + `Models/<the relevant settings class>.cs` + `Models/PipelineSettings.cs` if a new top-level group |
| New settings preset | `Models/ClayPresets.cs` |
| Slice pipeline orchestration | `UI/CCL_Clay3DPPanel.cs` → `OnSliceClick` / `RunPipeline` (Slice 2e) |
| Spiral toolpath generation | `Core/SpiralInterpolator.cs` + `Core/FrameComputer.cs` |
| Layer mode + bracing | `UI/CCL_Clay3DPPanel.cs` → `RunLayerSlice` + `Zigzag/` |
| Build volume / part-fits-cell logic | `Analysis/BuildVolumeCheck.cs` + post-slice block in `OnSliceClick` |
| Heatmap colors | `Analysis/HeatmapDisplay.cs` (the per-frame scoring is in `Analysis/PrintabilityAnalyzer.cs` but only `HeatmapDisplay` calls it currently) |
| RoboDK send / launch issues | `RoboDK/RoboDKSubprocess.cs` (Python script generator) + `UI/CCL_Clay3DPPanel.cs` → `PerformSendToRoboDK` |
| Status/detail labels | `UI/CCL_Clay3DPPanel.cs` → `SetStatus`, `SetDetail`, `WithTranslationNote` |

---

## 8. Recent slice history (context for understanding decisions)

| Slice | What landed | Why it matters here |
|---|---|---|
| 1 | Landscape 3-col SettingsDialog layout, nozzle moved to Tool group, Robot/Printer→Robot/Extruder | Dialog structure mirrors the conceptual sections in this doc |
| 2a | `WaterPercent` field (recorded only) | New Material section field; downstream behavior deferred until lab experiments produce a curve |
| 2b | Shrinkage compensation toggle + pipeline scale | The reason `_lastRawGeometry` exists (compounding shrinkage on auto-rebuild was the bug) |
| 2d | Build-volume check + Send block + popups | Catches oversized scaled parts before they crash the robot |
| 2e | RunPipeline extraction + Settings auto-rebuild via full pipeline | (in progress) Fixes shrinkage not refreshing on settings change |

Pending: Slice 2c (calibration database), Slice 3 (Build Volume W×D×H simplification), Slice 4 (Tool/Nozzle expansion = #12), RoboDK launch reliability (separate slice — needs failing log).
