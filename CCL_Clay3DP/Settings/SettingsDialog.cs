// Copyright 2026 CyberCraft Lab, OTH Regensburg, Prof. Christophe Barlieb
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Linq;
using Eto.Forms;
using Eto.Drawing;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Settings
{
    public class SettingsDialog : Dialog<bool>
    {
        private readonly PipelineSettings _settings;

        // Clay fields. MinLayerBondRatio is no longer exposed in the
        // dialog (Issue #16) but the model field stays — the printability
        // analyzer still reads it. Presets supply per-clay defaults.
        private DropDown _presetDropDown;
        private NumericStepper _beadDiameter;
        private NumericStepper _maxOverhang;
        private NumericStepper _materialDensity;
        private NumericStepper _waterPercent;
        private CheckBox _enableShrinkageCheck;
        private NumericStepper _shrinkagePercent;

        // Base fields (Issue #10) — multi-layer skirt + contour + 45-deg
        // cross-hatch infill raft for closed-loop / vase-style parts. The
        // dialog only exposes the on/off toggle and layer count; pattern
        // and line spacing are hardcoded (see BaseSettings doc). Settings
        // only in this commit; pipeline wired in follow-up slices.
        private CheckBox _enableBaseCheck;
        private NumericStepper _baseLayerCount;

        // Toolpath fields. RadialOffset and StartAngle were removed from
        // the dialog in Issue #16. RadialOffset was dead config and is
        // gone from the model too; StartAngle is still consumed by the
        // spiral interpolator and stays in the model with default 0.
        private CheckBox _spiralSliceCheck;
        private CheckBox _outerWallBracingCheck;
        private NumericStepper _bracingContactPoints;
        private CheckBox _spiralFollowsCurveNormalCheck;
        private NumericStepper _layerHeight;
        private DropDown _directionDropDown;
        private NumericStepper _framesPerLayer;

        // Robot fields. Tilt mode / LeadAngle / VerticalBias were dead
        // config (no pipeline ever read them) — removed from both the
        // dialog and the model in Issue #16.
        private NumericStepper _feedRate;
        private NumericStepper _maxWristAngularVel;
        private NumericStepper _spindleSpeed;
        private DropDown _nozzleTool;
        private TextBox _roboDKExePath;
        private TextBox _stationTemplatePath;
        private TextBox _projectName;

        // Build Volume fields. Slice 3 simplification: Width × Depth × Height
        // centered on world origin (build plate at world 0,0,0). Z always
        // starts at 0 (build plate); the model still exposes XMin/XMax/YMin/
        // YMax as computed properties for downstream consumers.
        private NumericStepper _buildVolumeWidth;
        private NumericStepper _buildVolumeDepth;
        private NumericStepper _buildVolumeHeight;

        // Set to true while we programmatically toggle the
        // _spiralFollowsCurveNormalCheck checkbox (LoadValues, mode-switch
        // force-uncheck, or post-warning revert) so the user-facing warning
        // popup only fires on a genuine user click.
        private bool _suppressSpiralNormalWarning = false;

        public SettingsDialog(PipelineSettings settings)
        {
            _settings = settings;
            Title = "CCL_Clay3DP Settings";
            // Landscape layout: three columns (Material+Volume / Tool+Toolpath / Robot).
            MinimumSize = new Size(1100, 520);
            Resizable = true;

            BuildUI();
            LoadValues();
        }

        private void BuildUI()
        {
            // --- Clay Material ---
            _presetDropDown = new DropDown();
            foreach (var name in ClayPresets.Names)
                _presetDropDown.Items.Add(name);
            _presetDropDown.SelectedValueChanged += OnPresetChanged;

            _beadDiameter = CreateStepper(0.5, 20.0, 0.5, 1);
            _maxOverhang = CreateStepper(1.0, 60.0, 1.0, 1);
            _materialDensity = CreateStepper(0.5, 5.0, 0.1, 1);
            // Water % range covers everything realistic: below ~3% is
            // unworkably dry, above ~25% is slip. User experiments are
            // currently in the 6-12% band.
            _waterPercent = CreateStepper(0.0, 30.0, 0.5, 1);

            // Shrinkage compensation — toggle gates the % stepper. Stoneware
            // total shrinkage typically 10-13%; range 0-25% covers anything
            // realistic. A future calibration database will auto-fill this
            // from measured per-material data.
            _enableShrinkageCheck = new CheckBox { Text = "Enable shrinkage compensation" };
            _enableShrinkageCheck.ToolTip =
                "When ON, the slice pipeline scales the input geometry " +
                "uniformly so the printed part (after drying + firing " +
                "shrinkage) lands at the size you modeled. Scale is applied " +
                "about the part footprint centroid on Z=0.";
            _enableShrinkageCheck.CheckedChanged += (s, e) => UpdateShrinkageFieldsEnabled();
            _shrinkagePercent = CreateStepper(0.0, 25.0, 0.5, 1);

            var clayGroup = new GroupBox
            {
                Text = "Clay Material",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        LabeledRow("Preset", _presetDropDown,
                            "Pre-defined clay parameter sets. Selecting a preset overwrites " +
                            "bead diameter, max overhang, and material density. \"Custom\" " +
                            "keeps your own values."),
                        LabeledRow("Bead diameter (mm)", _beadDiameter,
                            "Diameter of the extruded clay bead. Drives base infill line " +
                            "spacing (1.5x) and the bead-thickness assumption used by the " +
                            "printability analyzer."),
                        LabeledRow("Max overhang angle (deg)", _maxOverhang,
                            "Maximum surface tilt the wet clay will hold without sagging. " +
                            "Used by the printability analyzer to flag risky regions."),
                        LabeledRow("Material density (g/cm3)", _materialDensity,
                            "Mass per unit volume of the clay mix. Used to estimate part " +
                            "weight in the analyzer."),
                        LabeledRow("Water % added", _waterPercent,
                            "Water added during mix, as percentage of dry clay mass " +
                            "(additive: 6% = 60 g water per 1 kg dry clay). Higher water = " +
                            "more fluid (smaller nozzles work) but max overhang drops and " +
                            "drying shrinkage increases. Recorded only for now — does not " +
                            "yet drive nozzle / feedrate recommendations."),
                        new TableRow(null, _enableShrinkageCheck),
                        LabeledRow("Total shrinkage %", _shrinkagePercent,
                            "Combined drying + firing shrinkage of the chosen clay/mix. " +
                            "When the toggle above is ON, geometry is scaled up by " +
                            "1/(1-pct/100) so the post-firing part matches the modeled " +
                            "size. Stoneware typically 10-13%. Measure with a calibration " +
                            "bar to dial in your actual value."),
                    },
                },
            };

            // --- Toolpath ---
            // Base controls (Issue #10) are folded into the Toolpath group
            // so the dialog stays compact on a high-res laptop screen — the
            // base is part of the toolpath conceptually (it's the first
            // thing the robot prints). Infill pattern and line spacing are
            // hardcoded; see BaseSettings.
            _enableBaseCheck = new CheckBox { Text = "Enable base (multi-layer raft)" };
            _enableBaseCheck.ToolTip =
                "Print a multi-layer raft (skirt + N x contour + alternating " +
                "+/-45 deg crosshatch infill) under the part. Recommended for " +
                "closed-loop / vase parts with a hollow bottom.";
            _enableBaseCheck.CheckedChanged += (s, e) => UpdateBaseFieldsEnabled();

            // 2..10 layers per spec; integer (DecimalPlaces=0).
            _baseLayerCount = CreateStepper(2, 10, 1, 0);

            _spiralSliceCheck = new CheckBox { Text = "Spiral Slice (off = Layer Slice)" };
            _spiralSliceCheck.ToolTip =
                "ON: one continuous spiral from bottom to top (vase mode - " +
                "single-wall parts). OFF: discrete planar layers, each printed " +
                "as a closed loop.";
            _spiralSliceCheck.CheckedChanged += (s, e) => UpdateToolpathFieldsEnabled();
            _outerWallBracingCheck = new CheckBox
            {
                Text = "Outer Wall Bracing (Layer Slice only)",
                ToolTip =
                    "Layer Slice only. Adds a zigzag bracing pattern attached to " +
                    "the outer wall, anchored to a virtual inner offset. Improves " +
                    "rigidity of layered prints.",
            };
            _outerWallBracingCheck.CheckedChanged +=
                (s, e) => UpdateToolpathFieldsEnabled();
            // Any integer 4..500 is valid — the generator samples 2× this
            // many points internally so the alternating outer/inner pattern
            // closes cleanly regardless of parity.
            _bracingContactPoints = CreateStepper(4, 500, 1, 0);
            _spiralFollowsCurveNormalCheck = new CheckBox
            {
                Text = "Spiral follows curve normal (Spiral Slice only)",
                ToolTip =
                    "Spiral Slice only. Tilts the build plate so the tool aligns " +
                    "with the surface normal. WARNING: can drive the robot into " +
                    "joint limits, singularities, or self-collision - always " +
                    "review in RoboDK before sending.",
            };
            _spiralFollowsCurveNormalCheck.CheckedChanged +=
                OnSpiralFollowsCurveNormalChanged;

            _layerHeight = CreateStepper(0.1, 50.0, 0.5, 1);
            _directionDropDown = new DropDown();
            _directionDropDown.Items.Add("CCW");
            _directionDropDown.Items.Add("CW");
            _framesPerLayer = CreateStepper(36, 3600, 36, 0);

            var spiralGroup = new GroupBox
            {
                Text = "Toolpath",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        new TableRow(null, _enableBaseCheck),
                        LabeledRow("Base layer count (2-10)", _baseLayerCount,
                            "How many raft layers to print before the part body. " +
                            "Default 3 - more = stronger adhesion but more material and time."),
                        new TableRow(null, _spiralSliceCheck),
                        new TableRow(null, _outerWallBracingCheck),
                        LabeledRow("Bracing contact points (4-500)",
                            _bracingContactPoints,
                            "Number of times the bracing toolpath touches the outer " +
                            "wall around each layer — i.e. the number of \"kisses\" " +
                            "you can count by eye in the viewport. Decoupled from " +
                            "Frames per layer so bracing density is independent of " +
                            "toolpath sampling."),
                        new TableRow(null, _spiralFollowsCurveNormalCheck),
                        LabeledRow("Layer height (mm)", _layerHeight,
                            "Vertical distance between layers. For clay, typically " +
                            "0.5x-1.0x the bead diameter."),
                        LabeledRow("Direction", _directionDropDown,
                            "CCW: counter-clockwise spiral / contour winding. CW: clockwise."),
                        LabeledRow("Frames per layer", _framesPerLayer,
                            "Number of robot frames sampled per closed loop. Higher = " +
                            "smoother motion at the cost of larger G-code files and " +
                            "longer slice time. KUKA CNC has no per-program frame limit."),
                    },
                },
            };

            // --- Robot / Extruder ---
            _feedRate = CreateStepper(1.0, 500.0, 10.0, 1);
            _maxWristAngularVel = CreateStepper(1.0, 360.0, 5.0, 0);
            _spindleSpeed = CreateStepper(0, 9999, 50, 0);
            _nozzleTool = new DropDown();
            _nozzleTool.Items.Add("T10");
            _nozzleTool.Items.Add("T11");
            _nozzleTool.Items.Add("T12");

            _roboDKExePath = new TextBox();
            _stationTemplatePath = new TextBox();
            _projectName = new TextBox();

            var browseDKExe = new Button { Text = "..." };
            browseDKExe.Click += (s, e) => BrowseFile(_roboDKExePath, "Executable", "*.exe");
            var browseStation = new Button { Text = "..." };
            browseStation.Click += (s, e) => BrowseFile(_stationTemplatePath, "RoboDK Station", "*.rdk");

            // --- Build Volume ---
            // Slice 3: simplified to Width × Depth × Height (mm), centered
            // on the world origin in XY (build plate at world 0,0,0). Z
            // always starts at 0 (the build plate). Wide ranges so any
            // reasonable robot cell fits. The wireframe box bakes onto
            // layer "3DP::Build Volume" at slice time.
            _buildVolumeWidth  = CreateStepper(1.0, 5000.0, 10.0, 0);
            _buildVolumeDepth  = CreateStepper(1.0, 5000.0, 10.0, 0);
            _buildVolumeHeight = CreateStepper(1.0, 5000.0, 10.0, 0);

            var buildVolumeGroup = new GroupBox
            {
                Text = "Build Volume (mm)",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        LabeledRow("Width (X)", _buildVolumeWidth,
                            "Build plate width along X, in mm. The volume is centered " +
                            "on the world origin so X bounds are ±Width/2."),
                        LabeledRow("Depth (Y)", _buildVolumeDepth,
                            "Build plate depth along Y, in mm. The volume is centered " +
                            "on the world origin so Y bounds are ±Depth/2."),
                        LabeledRow("Height (Z)", _buildVolumeHeight,
                            "Maximum Z height of the printable workspace, in mm. Z always " +
                            "starts at 0 (build plate)."),
                    },
                },
            };

            // --- Tool / Nozzle ---
            // Nozzle dropdown lives in its own group (split out from Robot
            // section in the landscape redesign). Section is intentionally
            // sparse — expansion (diameter, tip offset, etc.) tracked under
            // issue #12.
            var toolGroup = new GroupBox
            {
                Text = "Tool / Nozzle",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        LabeledRow("Nozzle tool", _nozzleTool,
                            "RoboDK tool number - selects which calibrated tool offset to " +
                            "use. T10/T11/T12 must match what's defined in your station template."),
                    },
                },
            };

            var robotGroup = new GroupBox
            {
                Text = "Robot / Extruder",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        LabeledRow("Feed rate (mm/s)", _feedRate,
                            "Robot tool-tip travel speed during printing. Typical range " +
                            "30-60 mm/s for clay; coordinate with the extruder calibration " +
                            "(1-3 rotations/sec sweet spot)."),
                        LabeledRow("Max wrist vel. (deg/s)", _maxWristAngularVel,
                            "Hard cap on wrist angular velocity. Frames whose orientation " +
                            "change would exceed this cap trigger warnings - match to your " +
                            "KUKA model's joint limits."),
                        LabeledRow("Spindle speed (S value)", _spindleSpeed,
                            "Raw S-value passed to the extruder driver. Cell-specific: per " +
                            "CCL-ALTAR-01 calibration, RPM = 0.386*S + 14.94 for S >= 50; " +
                            "sweet spot S = 116-428 (1-3 rot/s). Front-panel ratio pot " +
                            "must be at 1."),
                        BrowseRow("RoboDK executable", _roboDKExePath, browseDKExe,
                            "Full path to RoboDK.exe. Use the Browse button if unsure."),
                        BrowseRow("Station template", _stationTemplatePath, browseStation,
                            "Path to the .rdk station file used as the starting point for " +
                            "each slice. Should contain your robot, build plate, and tool " +
                            "calibrations."),
                        LabeledRow("Project name", _projectName,
                            "Name used for the spawned RoboDK project - also affects output " +
                            "file naming."),
                    },
                },
            };

            // --- Buttons ---
            var okButton = new Button { Text = "OK" };
            okButton.Click += (s, e) =>
            {
                SaveValues();
                Close(true);
            };

            var cancelButton = new Button { Text = "Cancel" };
            cancelButton.Click += (s, e) => Close(false);

            var importButton = new Button { Text = "Import..." };
            importButton.ToolTip =
                "Load a previously-saved settings JSON file into the dialog. " +
                "Imported values are pending - they don't persist until you " +
                "click OK.";
            importButton.Click += OnImportClick;

            var exportButton = new Button { Text = "Export..." };
            exportButton.ToolTip =
                "Save the current dialog values to a JSON file for backup or " +
                "sharing. Does not modify the saved global settings - click OK " +
                "afterwards if you want the values persisted in the plugin too.";
            exportButton.Click += OnExportClick;

            DefaultButton = okButton;
            AbortButton = cancelButton;

            // Three landscape columns. Each column is a vertical stack of
            // GroupBoxes; column widths scale equally with dialog width.
            //   Left:   Clay Material   + Build Volume
            //   Middle: Tool / Nozzle   + Toolpath
            //   Right:  Robot / Extruder
            var leftColumn = new TableLayout
            {
                Spacing = new Size(0, 8),
                Rows =
                {
                    new TableRow(clayGroup),
                    new TableRow(buildVolumeGroup),
                    null,
                },
            };
            var middleColumn = new TableLayout
            {
                Spacing = new Size(0, 8),
                Rows =
                {
                    new TableRow(toolGroup),
                    new TableRow(spiralGroup),
                    null,
                },
            };
            var rightColumn = new TableLayout
            {
                Spacing = new Size(0, 8),
                Rows =
                {
                    new TableRow(robotGroup),
                    null,
                },
            };

            var columns = new TableLayout
            {
                Spacing = new Size(8, 0),
                Padding = new Padding(0),
                Rows =
                {
                    new TableRow(
                        new TableCell(leftColumn, true),
                        new TableCell(middleColumn, true),
                        new TableCell(rightColumn, true)),
                },
            };

            // The columns go inside a Scrollable so smaller laptop displays
            // get scrollbars instead of having the OK/Cancel row pushed
            // off-screen. The button row sits OUTSIDE the Scrollable so it
            // stays visible regardless of how the user scrolls.
            var scrollableContent = new Scrollable
            {
                Border = BorderType.None,
                ExpandContentWidth = true,
                ExpandContentHeight = false,
                Content = columns,
            };

            var buttonRow = new TableLayout
            {
                Spacing = new Size(8, 0),
                Rows =
                {
                    new TableRow(importButton, exportButton, null, cancelButton, okButton),
                },
            };

            Content = new TableLayout
            {
                Spacing = new Size(0, 8),
                Padding = new Padding(12),
                Rows =
                {
                    new TableRow(BuildHeader()),
                    new TableRow(new TableCell(scrollableContent, true)) { ScaleHeight = true },
                    new TableRow(buttonRow),
                },
            };
        }

        /// <summary>
        /// Header band shown above the settings groups: CC logo (40 px) +
        /// "CyberCraft Lab 3DP Interface" title in normal weight,
        /// left-aligned. The logo PNG is the same embedded resource used
        /// by the title-bar icon (see PluginIcon), loaded straight into
        /// an Eto Bitmap so the file is read once per dialog open.
        /// </summary>
        private static Control BuildHeader()
        {
            const int LogoSize = 40;
            Eto.Drawing.Image logoImage = null;
            try
            {
                var asm = typeof(SettingsDialog).Assembly;
                using (var stream = asm.GetManifestResourceStream("CCL_Clay3DP.CCLogo.png"))
                {
                    if (stream != null)
                        logoImage = new Bitmap(stream);
                }
            }
            catch
            {
                // Header degrades to title-only if the embedded resource
                // can't be read — a built plugin should never hit this.
            }

            var logoView = new ImageView
            {
                Image = logoImage,
                Size = new Size(LogoSize, LogoSize),
            };

            var titleLabel = new Label
            {
                Text = "CyberCraft Lab 3DP Interface",
                Font = new Font(SystemFont.Default, 14),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Logo + title side-by-side, left-aligned. The trailing null
            // cell soaks any extra horizontal space so the pair doesn't
            // stretch with the dialog width.
            return new TableLayout
            {
                Spacing = new Size(10, 0),
                Padding = new Padding(0, 0, 0, 4),
                Rows =
                {
                    new TableRow(logoView, titleLabel, null),
                },
            };
        }

        private void LoadValues()
        {
            // Clay
            int presetIndex = ClayPresets.Names.ToList().IndexOf(_settings.Clay.PresetName);
            _presetDropDown.SelectedIndex = presetIndex >= 0 ? presetIndex : 3; // Custom
            _beadDiameter.Value = _settings.Clay.BeadDiameter;
            _maxOverhang.Value = _settings.Clay.MaxOverhangAngle;
            _materialDensity.Value = _settings.Clay.MaterialDensity;
            _waterPercent.Value = _settings.Clay.WaterPercent;
            _enableShrinkageCheck.Checked = _settings.Clay.EnableShrinkageCompensation;
            // Clamp imported value into the stepper's range so out-of-spec
            // settings.json files don't blow up the dialog.
            double clampedShrinkage = _settings.Clay.ShrinkagePercent;
            if (clampedShrinkage < 0.0) clampedShrinkage = 0.0;
            if (clampedShrinkage > 25.0) clampedShrinkage = 25.0;
            _shrinkagePercent.Value = clampedShrinkage;

            // Base
            _enableBaseCheck.Checked = _settings.Base.EnableBase;
            // Clamp imported value into the stepper's range so out-of-spec
            // settings.json files don't blow up the dialog with a min/max
            // exception. Slice 2 will clamp again when the runtime consumes
            // the value.
            int clampedLayerCount = _settings.Base.LayerCount;
            if (clampedLayerCount < 2) clampedLayerCount = 2;
            if (clampedLayerCount > 10) clampedLayerCount = 10;
            _baseLayerCount.Value = clampedLayerCount;

            // Toolpath
            _spiralSliceCheck.Checked = _settings.Helix.SpiralSlice;
            _outerWallBracingCheck.Checked = _settings.Helix.OuterWallBracing;
            _suppressSpiralNormalWarning = true;
            _spiralFollowsCurveNormalCheck.Checked = _settings.Helix.SpiralFollowsCurveNormal;
            _suppressSpiralNormalWarning = false;
            _layerHeight.Value = _settings.Helix.LayerHeight;
            _directionDropDown.SelectedIndex = _settings.Helix.DirectionCCW ? 0 : 1;
            _framesPerLayer.Value = _settings.Helix.FramesPerLayer;
            // Clamp imported value to [4,500] so an out-of-spec settings.json
            // doesn't trip the stepper. No parity constraint — the generator
            // doubles this internally, guaranteeing an even sample count.
            int clampedBracingPts = _settings.Helix.BracingContactPoints;
            if (clampedBracingPts < 4) clampedBracingPts = 4;
            if (clampedBracingPts > 500) clampedBracingPts = 500;
            _bracingContactPoints.Value = clampedBracingPts;

            // Robot
            _feedRate.Value = _settings.Robot.FeedRate;
            _maxWristAngularVel.Value = _settings.Robot.MaxWristAngularVelocity;
            _spindleSpeed.Value = _settings.Robot.SpindleSpeed;
            int nozzleIdx = 0;
            if (_settings.Robot.NozzleTool == "T11") nozzleIdx = 1;
            else if (_settings.Robot.NozzleTool == "T12") nozzleIdx = 2;
            _nozzleTool.SelectedIndex = nozzleIdx;
            _roboDKExePath.Text = _settings.Robot.RoboDKExecutablePath;
            _stationTemplatePath.Text = _settings.Robot.RoboDKStationTemplatePath;
            _projectName.Text = _settings.Robot.RoboDKProjectName;

            // Build Volume
            _buildVolumeWidth.Value  = _settings.BuildVolume.Width;
            _buildVolumeDepth.Value  = _settings.BuildVolume.Depth;
            _buildVolumeHeight.Value = _settings.BuildVolume.Height;

            UpdateToolpathFieldsEnabled();
            UpdateBaseFieldsEnabled();
            UpdateShrinkageFieldsEnabled();
        }

        private void SaveValues()
        {
            ApplyDialogToSettings();
            SettingsManager.Save(_settings);
        }

        // Copy every dialog field into _settings without touching disk.
        // OK button writes _settings to the global config; Export writes
        // it to a user-chosen path. Both share this mutation step.
        private void ApplyDialogToSettings()
        {
            // Clay
            _settings.Clay.PresetName = _presetDropDown.SelectedValue?.ToString() ?? "Custom";
            _settings.Clay.BeadDiameter = _beadDiameter.Value;
            _settings.Clay.MaxOverhangAngle = _maxOverhang.Value;
            _settings.Clay.MaterialDensity = _materialDensity.Value;
            _settings.Clay.WaterPercent = _waterPercent.Value;
            _settings.Clay.EnableShrinkageCompensation = _enableShrinkageCheck.Checked ?? false;
            _settings.Clay.ShrinkagePercent = _shrinkagePercent.Value;

            // Base
            _settings.Base.EnableBase = _enableBaseCheck.Checked ?? false;
            _settings.Base.LayerCount = (int)_baseLayerCount.Value;

            // Toolpath
            _settings.Helix.SpiralSlice = _spiralSliceCheck.Checked ?? true;
            _settings.Helix.OuterWallBracing = _outerWallBracingCheck.Checked ?? false;
            _settings.Helix.SpiralFollowsCurveNormal = _spiralFollowsCurveNormalCheck.Checked ?? false;
            _settings.Helix.LayerHeight = _layerHeight.Value;
            _settings.Helix.DirectionCCW = _directionDropDown.SelectedIndex == 0;
            _settings.Helix.FramesPerLayer = (int)_framesPerLayer.Value;
            _settings.Helix.BracingContactPoints = (int)_bracingContactPoints.Value;

            // Robot
            _settings.Robot.FeedRate = _feedRate.Value;
            _settings.Robot.MaxWristAngularVelocity = _maxWristAngularVel.Value;
            _settings.Robot.SpindleSpeed = _spindleSpeed.Value;
            _settings.Robot.NozzleTool = _nozzleTool.SelectedValue?.ToString() ?? "T10";
            _settings.Robot.RoboDKExecutablePath = _roboDKExePath.Text;
            _settings.Robot.RoboDKStationTemplatePath = _stationTemplatePath.Text;
            _settings.Robot.RoboDKProjectName = _projectName.Text;

            // Build Volume
            _settings.BuildVolume.Width  = _buildVolumeWidth.Value;
            _settings.BuildVolume.Depth  = _buildVolumeDepth.Value;
            _settings.BuildVolume.Height = _buildVolumeHeight.Value;
        }

        private void OnPresetChanged(object sender, EventArgs e)
        {
            string name = _presetDropDown.SelectedValue?.ToString();
            if (name == null || name == "Custom")
                return;

            var preset = ClayPresets.Get(name);
            _beadDiameter.Value = preset.BeadDiameter;
            _maxOverhang.Value = preset.MaxOverhangAngle;
            _materialDensity.Value = preset.MaterialDensity;
            // Preset's MinLayerBondRatio still applied to the underlying
            // settings via _settings.Clay (PrintabilityAnalyzer reads it),
            // but no longer surfaced in the dialog — write straight in.
            _settings.Clay.MinLayerBondRatio = preset.MinLayerBondRatio;
        }

        /// <summary>
        /// Gray out base parameters when the feature is disabled. Mirrors
        /// the pattern used for tilt-mode fields and toolpath-mode fields.
        /// </summary>
        private void UpdateBaseFieldsEnabled()
        {
            bool on = _enableBaseCheck.Checked ?? false;
            _baseLayerCount.Enabled = on;
        }

        /// <summary>
        /// Gray out the shrinkage % stepper when compensation is disabled.
        /// </summary>
        private void UpdateShrinkageFieldsEnabled()
        {
            bool on = _enableShrinkageCheck.Checked ?? false;
            _shrinkagePercent.Enabled = on;
        }

        /// <summary>
        /// Gray out fields that only apply to one toolpath mode:
        ///  - Outer Wall Bracing: layer-slice only → disabled AND auto-unchecked when Spiral Slice
        ///  - Bracing contact points: only when Layer Slice AND bracing is on
        ///  - Spiral follows curve normal: spiral-only → disabled AND auto-unchecked when Layer Slice
        /// </summary>
        private void UpdateToolpathFieldsEnabled()
        {
            bool spiral = _spiralSliceCheck.Checked ?? true;
            _outerWallBracingCheck.Enabled = !spiral;
            _spiralFollowsCurveNormalCheck.Enabled = spiral;
            // Force-uncheck when a checkbox is inapplicable to the current
            // mode — leaving a grayed-but-checked box is confusing, and the
            // value is ignored anyway in the inactive mode. Suppress the
            // curve-normal warning popup since this is a programmatic toggle,
            // not a user click.
            if (spiral) _outerWallBracingCheck.Checked = false;
            if (!spiral)
            {
                _suppressSpiralNormalWarning = true;
                _spiralFollowsCurveNormalCheck.Checked = false;
                _suppressSpiralNormalWarning = false;
            }
            bool bracingActive = !spiral && (_outerWallBracingCheck.Checked ?? false);
            _bracingContactPoints.Enabled = bracingActive;
        }

        /// <summary>
        /// Fired when the user toggles the "Spiral follows curve normal"
        /// checkbox. On enable, shows a red-header WARNING modal — banking
        /// the build plate to follow surface normals can drive the robot
        /// into joint limits, singularities, or self-collision depending on
        /// part geometry. If the user cancels, the checkbox is reverted.
        /// </summary>
        private void OnSpiralFollowsCurveNormalChanged(object sender, EventArgs e)
        {
            if (_suppressSpiralNormalWarning) return;
            if (_spiralFollowsCurveNormalCheck.Checked != true) return;

            if (!ConfirmSpiralCurveNormalWarning())
            {
                _suppressSpiralNormalWarning = true;
                _spiralFollowsCurveNormalCheck.Checked = false;
                _suppressSpiralNormalWarning = false;
            }
        }

        /// <summary>
        /// Modal warning dialog with a red, bold "W A R N I N G !" header
        /// and Cancel as the safe default. Returns true if the user
        /// explicitly clicks "Continue anyway".
        /// </summary>
        private bool ConfirmSpiralCurveNormalWarning()
        {
            var dlg = new Dialog<bool>
            {
                Title = "CCL_Clay3DP — Surface-normal tool tilt",
                MinimumSize = new Size(480, 220),
                Resizable = false,
            };

            var headerLabel = new Label
            {
                Text = "W A R N I N G !",
                TextColor = Colors.Red,
                Font = new Font(SystemFont.Bold, 16),
                TextAlignment = TextAlignment.Center,
            };

            var bodyLabel = new Label
            {
                Text =
                    "Review your machining path carefully in RoboDK as you " +
                    "will likely crash your robot.\n\n" +
                    "This is to be used with serious caution.",
                Wrap = WrapMode.Word,
            };

            var continueBtn = new Button { Text = "Continue anyway" };
            continueBtn.Click += (s, e) => dlg.Close(true);
            var cancelBtn = new Button { Text = "Cancel" };
            cancelBtn.Click += (s, e) => dlg.Close(false);

            dlg.DefaultButton = cancelBtn;  // safe default — Enter cancels
            dlg.AbortButton = cancelBtn;    // Esc cancels

            dlg.Content = new TableLayout
            {
                Spacing = new Size(0, 14),
                Padding = new Padding(20),
                Rows =
                {
                    new TableRow(headerLabel),
                    new TableRow(bodyLabel),
                    null, // soak vertical space
                    new TableRow(new TableLayout
                    {
                        Spacing = new Size(8, 0),
                        Rows = { new TableRow(null, cancelBtn, continueBtn) },
                    }),
                },
            };

            return dlg.ShowModal(this);
        }

        private static NumericStepper CreateStepper(double min, double max, double increment, int decimals)
        {
            return new NumericStepper
            {
                MinValue = min,
                MaxValue = max,
                Increment = increment,
                DecimalPlaces = decimals,
                Width = 120,
            };
        }

        // Optional tip parameter (Issue #16). When non-null, the tooltip
        // is applied to BOTH the label and the control so hovering either
        // surface shows the help text. Eto wraps long tips automatically.
        private static TableRow LabeledRow(string label, Control control, string tip = null)
        {
            var labelCtrl = new Label
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (tip != null)
            {
                labelCtrl.ToolTip = tip;
                control.ToolTip = tip;
            }
            return new TableRow(labelCtrl, control);
        }

        // Same tip parameter as LabeledRow — when non-null, sets the
        // tooltip on the label, the textbox, and the Browse button so
        // hovering any of the three shows the help.
        private static TableRow BrowseRow(string label, TextBox textBox, Button browseButton, string tip = null)
        {
            var labelCtrl = new Label
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (tip != null)
            {
                labelCtrl.ToolTip = tip;
                textBox.ToolTip = tip;
                browseButton.ToolTip = tip;
            }
            var row = new TableLayout
            {
                Spacing = new Size(4, 0),
                Rows = { new TableRow(new TableCell(textBox, true), browseButton) },
            };
            return new TableRow(labelCtrl, row);
        }

        private void BrowseFile(TextBox target, string filterName, string filterExt)
        {
            var dialog = new OpenFileDialog();
            dialog.Filters.Add(new FileFilter(filterName, filterExt));
            if (dialog.ShowDialog(this) == DialogResult.Ok)
                target.Text = dialog.FileName;
        }

        // Bake current dialog values into _settings, then write that
        // snapshot to a user-chosen JSON file. Strictly side-effect-free
        // for the global config — the user must still click OK to persist.
        private void OnExportClick(object sender, EventArgs e)
        {
            ApplyDialogToSettings();

            var sfd = new SaveFileDialog
            {
                Title = "Export CCL_Clay3DP Settings",
                FileName = "ccl_clay3dp_settings.json",
            };
            sfd.Filters.Add(new FileFilter("Settings (*.json)", "*.json"));
            if (sfd.ShowDialog(this) != DialogResult.Ok) return;

            string path = sfd.FileName;
            // Eto on some platforms doesn't auto-append the filter extension.
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                path += ".json";

            try
            {
                SettingsManager.SaveTo(_settings, path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not write settings file:\n{ex.Message}",
                    "Export failed", MessageBoxType.Error);
            }
        }

        // Read settings from a user-chosen JSON file into _settings, then
        // refresh every dialog field from those values. Imported values are
        // pending until the user clicks OK.
        private void OnImportClick(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "Import CCL_Clay3DP Settings",
            };
            ofd.Filters.Add(new FileFilter("Settings (*.json)", "*.json"));
            if (ofd.ShowDialog(this) != DialogResult.Ok) return;

            PipelineSettings loaded;
            try
            {
                loaded = SettingsManager.LoadFrom(ofd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not read settings file:\n{ex.Message}",
                    "Import failed", MessageBoxType.Error);
                return;
            }

            _settings.Clay = loaded.Clay;
            _settings.Height = loaded.Height;
            _settings.Helix = loaded.Helix;
            _settings.Robot = loaded.Robot;
            if (loaded.BuildVolume != null)
                _settings.BuildVolume = loaded.BuildVolume;
            // Pre-#10 settings files won't carry a Base block; keep
            // whatever defaults the dialog already had so the user isn't
            // dropped into a half-configured state.
            if (loaded.Base != null)
                _settings.Base = loaded.Base;
            LoadValues();
        }
    }
}
