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

        // Clay fields
        private DropDown _presetDropDown;
        private NumericStepper _beadDiameter;
        private NumericStepper _maxOverhang;
        private NumericStepper _minBondRatio;
        private NumericStepper _materialDensity;

        // Base fields (Issue #10) — multi-layer skirt + contour + 45-deg
        // cross-hatch infill raft for closed-loop / vase-style parts. The
        // dialog only exposes the on/off toggle and layer count; pattern
        // and line spacing are hardcoded (see BaseSettings doc). Settings
        // only in this commit; pipeline wired in follow-up slices.
        private CheckBox _enableBaseCheck;
        private NumericStepper _baseLayerCount;

        // Toolpath fields
        private CheckBox _spiralSliceCheck;
        private CheckBox _outerWallBracingCheck;
        private CheckBox _spiralFollowsCurveNormalCheck;
        private NumericStepper _layerHeight;
        private NumericStepper _radialOffset;
        private NumericStepper _startAngle;
        private DropDown _directionDropDown;
        private NumericStepper _framesPerLayer;

        // Robot fields
        private NumericStepper _feedRate;
        private DropDown _tiltModeDropDown;
        private NumericStepper _leadAngle;
        private NumericStepper _verticalBias;
        private NumericStepper _maxWristAngularVel;
        private NumericStepper _spindleSpeed;
        private DropDown _nozzleTool;
        private TextBox _roboDKExePath;
        private TextBox _stationTemplatePath;
        private TextBox _projectName;

        // Build Volume fields (Issue #8 follow-up). Z always starts at 0
        // (build plate); only the X/Y bounds and the upper Z height are
        // user-settable.
        private NumericStepper _buildVolumeXMin;
        private NumericStepper _buildVolumeXMax;
        private NumericStepper _buildVolumeYMin;
        private NumericStepper _buildVolumeYMax;
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
            MinimumSize = new Size(460, 600);
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
            _minBondRatio = CreateStepper(0.1, 1.0, 0.05, 2);
            _materialDensity = CreateStepper(0.5, 5.0, 0.1, 1);

            var clayGroup = new GroupBox
            {
                Text = "Clay Material",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        LabeledRow("Preset", _presetDropDown),
                        LabeledRow("Bead diameter (mm)", _beadDiameter),
                        LabeledRow("Max overhang angle (deg)", _maxOverhang),
                        LabeledRow("Min layer bond ratio", _minBondRatio),
                        LabeledRow("Material density (g/cm3)", _materialDensity),
                    },
                },
            };

            // --- Base (Issue #10) ---
            // Sits between Clay and Toolpath because the base is a
            // material-driven feature (line spacing follows bead diameter)
            // and conceptually precedes the part toolpath. Infill pattern
            // and line spacing are hardcoded — see BaseSettings.
            _enableBaseCheck = new CheckBox { Text = "Enable base (multi-layer raft)" };
            _enableBaseCheck.CheckedChanged += (s, e) => UpdateBaseFieldsEnabled();

            // 2..10 layers per spec; integer (DecimalPlaces=0).
            _baseLayerCount = CreateStepper(2, 10, 1, 0);

            var baseGroup = new GroupBox
            {
                Text = "Base",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        new TableRow(null, _enableBaseCheck),
                        LabeledRow("Base layer count (2-10)", _baseLayerCount),
                    },
                },
            };

            // --- Toolpath ---
            _spiralSliceCheck = new CheckBox { Text = "Spiral Slice (off = Layer Slice)" };
            _spiralSliceCheck.CheckedChanged += (s, e) => UpdateToolpathFieldsEnabled();
            _outerWallBracingCheck = new CheckBox
            {
                Text = "Outer Wall Bracing (Layer Slice only)"
            };
            _spiralFollowsCurveNormalCheck = new CheckBox
            {
                Text = "Spiral follows curve normal (Spiral Slice only)"
            };
            _spiralFollowsCurveNormalCheck.CheckedChanged +=
                OnSpiralFollowsCurveNormalChanged;

            _layerHeight = CreateStepper(0.1, 50.0, 0.5, 1);
            _radialOffset = CreateStepper(-100.0, 100.0, 0.5, 1);
            _startAngle = CreateStepper(0.0, 360.0, 5.0, 0);
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
                        new TableRow(null, _spiralSliceCheck),
                        new TableRow(null, _outerWallBracingCheck),
                        new TableRow(null, _spiralFollowsCurveNormalCheck),
                        LabeledRow("Layer height (mm)", _layerHeight),
                        LabeledRow("Radial offset (mm)", _radialOffset),
                        LabeledRow("Start angle (deg)", _startAngle),
                        LabeledRow("Direction", _directionDropDown),
                        LabeledRow("Frames per layer", _framesPerLayer),
                    },
                },
            };

            // --- Robot / Printer ---
            _feedRate = CreateStepper(1.0, 500.0, 10.0, 1);
            _tiltModeDropDown = new DropDown();
            _tiltModeDropDown.Items.Add("Normal");
            _tiltModeDropDown.Items.Add("Lead-Lag");
            _tiltModeDropDown.Items.Add("Vertical Bias");
            _tiltModeDropDown.SelectedValueChanged += OnTiltModeChanged;
            _leadAngle = CreateStepper(-45.0, 45.0, 1.0, 1);
            _verticalBias = CreateStepper(0.0, 1.0, 0.05, 2);
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
            // Wide ranges so any reasonable robot cell fits. The wireframe
            // box bakes onto layer "3DP::Build Volume" at slice time.
            _buildVolumeXMin = CreateStepper(-5000.0, 0.0, 10.0, 0);
            _buildVolumeXMax = CreateStepper(0.0, 5000.0, 10.0, 0);
            _buildVolumeYMin = CreateStepper(-5000.0, 0.0, 10.0, 0);
            _buildVolumeYMax = CreateStepper(0.0, 5000.0, 10.0, 0);
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
                        LabeledRow("X min", _buildVolumeXMin),
                        LabeledRow("X max", _buildVolumeXMax),
                        LabeledRow("Y min", _buildVolumeYMin),
                        LabeledRow("Y max", _buildVolumeYMax),
                        LabeledRow("Z height (Z min always 0)", _buildVolumeHeight),
                    },
                },
            };

            var robotGroup = new GroupBox
            {
                Text = "Robot / Printer",
                Content = new TableLayout
                {
                    Spacing = new Size(8, 4),
                    Padding = new Padding(8),
                    Rows =
                    {
                        LabeledRow("Feed rate (mm/s)", _feedRate),
                        LabeledRow("Tilt mode", _tiltModeDropDown),
                        LabeledRow("Lead angle (deg)", _leadAngle),
                        LabeledRow("Vertical bias (0-1)", _verticalBias),
                        LabeledRow("Max wrist vel. (deg/s)", _maxWristAngularVel),
                        LabeledRow("Spindle speed (S value)", _spindleSpeed),
                        LabeledRow("Nozzle tool", _nozzleTool),
                        BrowseRow("RoboDK executable", _roboDKExePath, browseDKExe),
                        BrowseRow("Station template", _stationTemplatePath, browseStation),
                        LabeledRow("Project name", _projectName),
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
            importButton.Click += OnImportClick;

            var exportButton = new Button { Text = "Export..." };
            exportButton.Click += OnExportClick;

            DefaultButton = okButton;
            AbortButton = cancelButton;

            // The settings groups go inside a Scrollable so smaller laptop
            // displays get scrollbars instead of having the OK/Cancel row
            // pushed off-screen. The button row sits OUTSIDE the Scrollable
            // so it stays visible regardless of how the user scrolls.
            var scrollableContent = new Scrollable
            {
                Border = BorderType.None,
                ExpandContentWidth = true,
                ExpandContentHeight = false,
                Content = new TableLayout
                {
                    Spacing = new Size(0, 8),
                    Padding = new Padding(0),
                    Rows =
                    {
                        new TableRow(clayGroup),
                        new TableRow(baseGroup),
                        new TableRow(spiralGroup),
                        new TableRow(robotGroup),
                        new TableRow(buildVolumeGroup),
                        null, // soak any extra vertical space inside the scroller
                    },
                },
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
                    new TableRow(new TableCell(scrollableContent, true)) { ScaleHeight = true },
                    new TableRow(buttonRow),
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
            _minBondRatio.Value = _settings.Clay.MinLayerBondRatio;
            _materialDensity.Value = _settings.Clay.MaterialDensity;

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
            _radialOffset.Value = _settings.Helix.RadialOffset;
            _startAngle.Value = _settings.Helix.StartAngle;
            _directionDropDown.SelectedIndex = _settings.Helix.DirectionCCW ? 0 : 1;
            _framesPerLayer.Value = _settings.Helix.FramesPerLayer;

            // Robot
            _feedRate.Value = _settings.Robot.FeedRate;
            _tiltModeDropDown.SelectedIndex = (int)_settings.Robot.TiltMode;
            _leadAngle.Value = _settings.Robot.LeadAngle;
            _verticalBias.Value = _settings.Robot.VerticalBias;
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
            _buildVolumeXMin.Value = _settings.BuildVolume.XMin;
            _buildVolumeXMax.Value = _settings.BuildVolume.XMax;
            _buildVolumeYMin.Value = _settings.BuildVolume.YMin;
            _buildVolumeYMax.Value = _settings.BuildVolume.YMax;
            _buildVolumeHeight.Value = _settings.BuildVolume.Height;

            UpdateTiltFieldsEnabled();
            UpdateToolpathFieldsEnabled();
            UpdateBaseFieldsEnabled();
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
            _settings.Clay.MinLayerBondRatio = _minBondRatio.Value;
            _settings.Clay.MaterialDensity = _materialDensity.Value;

            // Base
            _settings.Base.EnableBase = _enableBaseCheck.Checked ?? false;
            _settings.Base.LayerCount = (int)_baseLayerCount.Value;

            // Toolpath
            _settings.Helix.SpiralSlice = _spiralSliceCheck.Checked ?? true;
            _settings.Helix.OuterWallBracing = _outerWallBracingCheck.Checked ?? false;
            _settings.Helix.SpiralFollowsCurveNormal = _spiralFollowsCurveNormalCheck.Checked ?? false;
            _settings.Helix.LayerHeight = _layerHeight.Value;
            _settings.Helix.RadialOffset = _radialOffset.Value;
            _settings.Helix.StartAngle = _startAngle.Value;
            _settings.Helix.DirectionCCW = _directionDropDown.SelectedIndex == 0;
            _settings.Helix.FramesPerLayer = (int)_framesPerLayer.Value;

            // Robot
            _settings.Robot.FeedRate = _feedRate.Value;
            _settings.Robot.TiltMode = (TiltMode)_tiltModeDropDown.SelectedIndex;
            _settings.Robot.LeadAngle = _leadAngle.Value;
            _settings.Robot.VerticalBias = _verticalBias.Value;
            _settings.Robot.MaxWristAngularVelocity = _maxWristAngularVel.Value;
            _settings.Robot.SpindleSpeed = _spindleSpeed.Value;
            _settings.Robot.NozzleTool = _nozzleTool.SelectedValue?.ToString() ?? "T10";
            _settings.Robot.RoboDKExecutablePath = _roboDKExePath.Text;
            _settings.Robot.RoboDKStationTemplatePath = _stationTemplatePath.Text;
            _settings.Robot.RoboDKProjectName = _projectName.Text;

            // Build Volume
            _settings.BuildVolume.XMin = _buildVolumeXMin.Value;
            _settings.BuildVolume.XMax = _buildVolumeXMax.Value;
            _settings.BuildVolume.YMin = _buildVolumeYMin.Value;
            _settings.BuildVolume.YMax = _buildVolumeYMax.Value;
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
            _minBondRatio.Value = preset.MinLayerBondRatio;
            _materialDensity.Value = preset.MaterialDensity;
        }

        private void OnTiltModeChanged(object sender, EventArgs e)
        {
            UpdateTiltFieldsEnabled();
        }

        private void UpdateTiltFieldsEnabled()
        {
            var mode = (TiltMode)_tiltModeDropDown.SelectedIndex;
            _leadAngle.Enabled = mode == TiltMode.LeadLag;
            _verticalBias.Enabled = mode == TiltMode.VerticalBias;
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
        /// Gray out fields that only apply to one toolpath mode:
        ///  - Radial offset, Start angle: spiral-only → disabled when Layer Slice
        ///  - Outer Wall Bracing: layer-slice only → disabled AND auto-unchecked when Spiral Slice
        ///  - Spiral follows curve normal: spiral-only → disabled AND auto-unchecked when Layer Slice
        /// </summary>
        private void UpdateToolpathFieldsEnabled()
        {
            bool spiral = _spiralSliceCheck.Checked ?? true;
            _radialOffset.Enabled = spiral;
            _startAngle.Enabled = spiral;
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

        private static TableRow LabeledRow(string label, Control control)
        {
            return new TableRow(
                new Label { Text = label, VerticalAlignment = VerticalAlignment.Center },
                control);
        }

        private static TableRow BrowseRow(string label, TextBox textBox, Button browseButton)
        {
            var row = new TableLayout
            {
                Spacing = new Size(4, 0),
                Rows = { new TableRow(new TableCell(textBox, true), browseButton) },
            };
            return new TableRow(
                new Label { Text = label, VerticalAlignment = VerticalAlignment.Center },
                row);
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
