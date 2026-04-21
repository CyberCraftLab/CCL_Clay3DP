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

        // Toolpath fields
        private CheckBox _spiralSliceCheck;
        private CheckBox _innerWallBracingCheck;
        private NumericStepper _layerHeight;
        private NumericStepper _radialOffset;
        private NumericStepper _startAngle;
        private DropDown _directionDropDown;
        private NumericStepper _framesPerLayer;
        private NumericStepper _heightOffsetBottom;
        private NumericStepper _heightOffsetTop;
        private NumericStepper _ribbonWidth;

        // Robot fields
        private NumericStepper _feedRate;
        private NumericStepper _travelSpeed;
        private DropDown _tiltModeDropDown;
        private NumericStepper _leadAngle;
        private NumericStepper _verticalBias;
        private NumericStepper _maxWristAngularVel;
        private NumericStepper _spindleSpeed;
        private DropDown _nozzleTool;
        private TextBox _roboDKExePath;
        private TextBox _stationTemplatePath;
        private TextBox _projectName;

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

            // --- Toolpath ---
            _spiralSliceCheck = new CheckBox { Text = "Spiral Slice (off = Layer Slice)" };
            _spiralSliceCheck.CheckedChanged += (s, e) => UpdateToolpathFieldsEnabled();
            _innerWallBracingCheck = new CheckBox
            {
                Text = "Inner Wall Bracing (Layer Slice only)"
            };

            _layerHeight = CreateStepper(0.1, 50.0, 0.5, 1);
            _radialOffset = CreateStepper(-100.0, 100.0, 0.5, 1);
            _startAngle = CreateStepper(0.0, 360.0, 5.0, 0);
            _directionDropDown = new DropDown();
            _directionDropDown.Items.Add("CCW");
            _directionDropDown.Items.Add("CW");
            _framesPerLayer = CreateStepper(36, 3600, 36, 0);
            _heightOffsetBottom = CreateStepper(0.0, 1000.0, 1.0, 1);
            _heightOffsetTop = CreateStepper(0.0, 1000.0, 1.0, 1);
            _ribbonWidth = CreateStepper(0.1, 100.0, 0.5, 1);

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
                        new TableRow(null, _innerWallBracingCheck),
                        LabeledRow("Layer height (mm)", _layerHeight),
                        LabeledRow("Radial offset (mm)", _radialOffset),
                        LabeledRow("Start angle (deg)", _startAngle),
                        LabeledRow("Direction", _directionDropDown),
                        LabeledRow("Frames per layer", _framesPerLayer),
                        LabeledRow("Height offset bottom (mm)", _heightOffsetBottom),
                        LabeledRow("Height offset top (mm)", _heightOffsetTop),
                        LabeledRow("Ribbon width (mm)", _ribbonWidth),
                    },
                },
            };

            // --- Robot / Printer ---
            _feedRate = CreateStepper(1.0, 500.0, 10.0, 1);
            _travelSpeed = CreateStepper(1.0, 500.0, 10.0, 1);
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
                        LabeledRow("Travel speed (mm/s)", _travelSpeed),
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

            DefaultButton = okButton;
            AbortButton = cancelButton;

            Content = new TableLayout
            {
                Spacing = new Size(0, 8),
                Padding = new Padding(12),
                Rows =
                {
                    new TableRow(clayGroup),
                    new TableRow(spiralGroup),
                    new TableRow(robotGroup),
                    new TableRow(new TableLayout
                    {
                        Spacing = new Size(8, 0),
                        Rows =
                        {
                            new TableRow(null, cancelButton, okButton),
                        },
                    }),
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

            // Toolpath
            _spiralSliceCheck.Checked = _settings.Helix.SpiralSlice;
            _innerWallBracingCheck.Checked = _settings.Helix.InnerWallBracing;
            _layerHeight.Value = _settings.Helix.LayerHeight;
            _radialOffset.Value = _settings.Helix.RadialOffset;
            _startAngle.Value = _settings.Helix.StartAngle;
            _directionDropDown.SelectedIndex = _settings.Helix.DirectionCCW ? 0 : 1;
            _framesPerLayer.Value = _settings.Helix.FramesPerLayer;
            _heightOffsetBottom.Value = _settings.Height.HeightOffsetBottom;
            _heightOffsetTop.Value = _settings.Height.HeightOffsetTop;
            _ribbonWidth.Value = _settings.Ribbon.RibbonWidth;

            // Robot
            _feedRate.Value = _settings.Robot.FeedRate;
            _travelSpeed.Value = _settings.Robot.TravelSpeed;
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

            UpdateTiltFieldsEnabled();
            UpdateToolpathFieldsEnabled();
        }

        private void SaveValues()
        {
            // Clay
            _settings.Clay.PresetName = _presetDropDown.SelectedValue?.ToString() ?? "Custom";
            _settings.Clay.BeadDiameter = _beadDiameter.Value;
            _settings.Clay.MaxOverhangAngle = _maxOverhang.Value;
            _settings.Clay.MinLayerBondRatio = _minBondRatio.Value;
            _settings.Clay.MaterialDensity = _materialDensity.Value;

            // Toolpath
            _settings.Helix.SpiralSlice = _spiralSliceCheck.Checked ?? true;
            _settings.Helix.InnerWallBracing = _innerWallBracingCheck.Checked ?? false;
            _settings.Helix.LayerHeight = _layerHeight.Value;
            _settings.Helix.RadialOffset = _radialOffset.Value;
            _settings.Helix.StartAngle = _startAngle.Value;
            _settings.Helix.DirectionCCW = _directionDropDown.SelectedIndex == 0;
            _settings.Helix.FramesPerLayer = (int)_framesPerLayer.Value;
            _settings.Height.HeightOffsetBottom = _heightOffsetBottom.Value;
            _settings.Height.HeightOffsetTop = _heightOffsetTop.Value;
            _settings.Ribbon.RibbonWidth = _ribbonWidth.Value;

            // Robot
            _settings.Robot.FeedRate = _feedRate.Value;
            _settings.Robot.TravelSpeed = _travelSpeed.Value;
            _settings.Robot.TiltMode = (TiltMode)_tiltModeDropDown.SelectedIndex;
            _settings.Robot.LeadAngle = _leadAngle.Value;
            _settings.Robot.VerticalBias = _verticalBias.Value;
            _settings.Robot.MaxWristAngularVelocity = _maxWristAngularVel.Value;
            _settings.Robot.SpindleSpeed = _spindleSpeed.Value;
            _settings.Robot.NozzleTool = _nozzleTool.SelectedValue?.ToString() ?? "T10";
            _settings.Robot.RoboDKExecutablePath = _roboDKExePath.Text;
            _settings.Robot.RoboDKStationTemplatePath = _stationTemplatePath.Text;
            _settings.Robot.RoboDKProjectName = _projectName.Text;

            SettingsManager.Save(_settings);
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
        /// Gray out fields that only apply to one toolpath mode:
        ///  - Radial offset, Start angle, Ribbon width: spiral-only → disabled when Layer Slice
        ///  - Inner Wall Bracing: layer-slice only → disabled AND auto-unchecked when Spiral Slice
        /// </summary>
        private void UpdateToolpathFieldsEnabled()
        {
            bool spiral = _spiralSliceCheck.Checked ?? true;
            _radialOffset.Enabled = spiral;
            _startAngle.Enabled = spiral;
            _ribbonWidth.Enabled = spiral;
            _innerWallBracingCheck.Enabled = !spiral;
            // Force-uncheck when spiraling — leaving a grayed-but-checked box
            // is confusing and the value is ignored anyway in spiral mode.
            if (spiral) _innerWallBracingCheck.Checked = false;
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
    }
}
