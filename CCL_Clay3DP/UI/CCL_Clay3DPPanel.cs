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
using System.Collections.Generic;
using Eto.Forms;
using Eto.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;
using CCL_Clay3DP.Analysis;
using CCL_Clay3DP.Core;
using CCL_Clay3DP.Models;
using CCL_Clay3DP.Settings;

namespace CCL_Clay3DP.UI
{
    [System.Runtime.InteropServices.Guid("b3a7c1d2-4e5f-6a7b-8c9d-0e1f2a3b4c5d")]
    public class CCL_Clay3DPPanel : Panel
    {
        private PipelineSettings _settings;
        private Label _statusLabel;
        private Label _detailLabel;
        private DropDown _analysisChannel;

        private SpiralResult _lastResult;
        private GeometrySelection _lastGeometry;

        public CCL_Clay3DPPanel()
        {
            _settings = SettingsManager.Load();
            BuildUI();
        }

        private void BuildUI()
        {
            // Settings button
            var settingsButton = new Button { Text = "Settings" };
            settingsButton.Click += OnSettingsClick;

            // Workflow buttons
            var sliceButton = new Button { Text = "1. Spiral Slice" };
            sliceButton.Click += OnSpiralSliceClick;

            _analysisChannel = new DropDown();
            _analysisChannel.Items.Add("Clay");
            _analysisChannel.Items.Add("Robot");
            _analysisChannel.Items.Add("Both");
            _analysisChannel.SelectedIndex = 0;

            var analyzeButton = new Button { Text = "2. Analyze" };
            analyzeButton.Click += OnAnalyzeClick;

            var analyzeRow = new TableLayout
            {
                Spacing = new Size(4, 0),
                Rows = { new TableRow(new TableCell(analyzeButton, true), _analysisChannel) },
            };

            var sendButton = new Button { Text = "3. Send to RoboDK" };
            sendButton.Click += OnSendToRoboDKClick;

            var runAllButton = new Button { Text = "Run All" };
            runAllButton.Click += OnRunAllClick;

            // Status
            _statusLabel = new Label { Text = "Status: Ready" };
            _detailLabel = new Label { Text = "Frames: 0  |  Issues: 0" };

            Content = new TableLayout
            {
                Spacing = new Size(0, 6),
                Padding = new Padding(8),
                Rows =
                {
                    new TableRow(settingsButton),
                    new TableRow(new Divider()),
                    new TableRow(sliceButton),
                    new TableRow(analyzeRow),
                    new TableRow(sendButton),
                    new TableRow(new Divider()),
                    new TableRow(runAllButton),
                    new TableRow(new Divider()),
                    new TableRow(_statusLabel),
                    new TableRow(_detailLabel),
                    null, // fill remaining space
                },
            };
        }

        // Layers the plugin generates during Spiral Slice and Analyze.
        // Used to detect stale generated geometry after a settings change
        // and to clear it on the user's confirmation.
        private static readonly string[] GeneratedLayerNames = new[]
        {
            "3DP::Contours",
            "3DP::Spiral Toolpath",
            "3DP::Ribbon",
            "3DP::Heatmap",
        };

        private void OnSettingsClick(object sender, EventArgs e)
        {
            var dialog = new SettingsDialog(_settings);
            if (!dialog.ShowModal(this)) return;

            _settings = SettingsManager.Load();
            SetStatus("Settings saved");

            // After a settings change, any previously generated spiral /
            // ribbon / heatmap geometry — and the cached slice result used
            // by Send to RoboDK — were computed with the OLD settings and
            // are now stale. Clear the generated layers unconditionally,
            // then auto-rebuild from the same geometry if we still have a
            // reference to it. This avoids the easy-to-miss "remember to
            // re-run Spiral Slice" failure mode.
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            if (!HasGeneratedContent(doc)) return;

            var geometryToRebuild = _lastGeometry;
            ClearGeneratedContent(doc);
            _lastResult = null;
            _lastGeometry = null;

            if (geometryToRebuild != null)
            {
                SetStatus("Settings changed — regenerating spiral...");
                RhinoApp.Wait();
                RunSliceAndBake(geometryToRebuild);

                // If the user has already pushed this job to RoboDK in this
                // session, keep RoboDK in sync with the new settings without
                // making them click Send again. We skip the confirm dialog
                // because the user just explicitly asked for this change by
                // saving settings.
                if (_hasSentToRoboDK && _lastResult != null
                    && _lastResult.Frames.Count > 0)
                {
                    SetStatus("Settings changed — updating RoboDK template...");
                    RhinoApp.Wait();
                    PerformSendToRoboDK();
                }
            }
            else
            {
                SetStatus("Generated layers cleared — re-run Spiral Slice");
            }
        }

        private bool HasGeneratedContent(RhinoDoc doc)
        {
            if (_lastResult != null) return true;
            var indices = FindGeneratedLayerIndices(doc);
            if (indices.Count == 0) return false;
            foreach (var obj in doc.Objects)
            {
                if (obj == null || obj.IsDeleted) continue;
                if (indices.Contains(obj.Attributes.LayerIndex)) return true;
            }
            return false;
        }

        // The plugin creates its layers as nested Rhino layers — e.g.
        // '3DP::Contours' is a layer named 'Contours' whose parent is '3DP'.
        // RhinoDoc.Layers.FindName() only matches the leaf '.Name', so it
        // returns null for full paths. FindByFullPath() is the correct
        // lookup for the '::' style paths the plugin generates.
        private static System.Collections.Generic.HashSet<int>
            FindGeneratedLayerIndices(RhinoDoc doc)
        {
            var indices = new System.Collections.Generic.HashSet<int>();
            foreach (var name in GeneratedLayerNames)
            {
                int idx = doc.Layers.FindByFullPath(name, -1);
                if (idx >= 0) indices.Add(idx);
            }
            return indices;
        }

        private void ClearGeneratedContent(RhinoDoc doc)
        {
            var targetIndices = FindGeneratedLayerIndices(doc);
            if (targetIndices.Count == 0)
            {
                RhinoApp.WriteLine("[CCL_Clay3DP] No generated layers found to clear");
                return;
            }

            // Unlock any target layer that was locked, else Delete() refuses.
            foreach (int idx in targetIndices)
            {
                var layer = doc.Layers[idx];
                if (layer != null && layer.IsLocked) layer.IsLocked = false;
            }

            // Snapshot first — deleting while enumerating doc.Objects is unsafe.
            var victims = new System.Collections.Generic.List<Rhino.DocObjects.RhinoObject>();
            foreach (var obj in doc.Objects)
            {
                if (obj == null || obj.IsDeleted) continue;
                if (targetIndices.Contains(obj.Attributes.LayerIndex))
                    victims.Add(obj);
            }

            int deleted = 0, failed = 0;
            foreach (var obj in victims)
            {
                if (doc.Objects.Delete(obj, true)) deleted++;
                else failed++;
            }
            doc.Views.Redraw();
            RhinoApp.WriteLine(
                $"[CCL_Clay3DP] Cleared {deleted} generated object(s)" +
                (failed > 0 ? $" ({failed} failed to delete)" : ""));
        }

        private void OnSpiralSliceClick(object sender, EventArgs e)
        {
            SetStatus("Selecting geometry...");
            var selection = GeometrySelector.Select();
            if (selection == null)
            {
                SetStatus("Cancelled");
                return;
            }
            RunSliceAndBake(selection);
        }

        private void RunSliceAndBake(GeometrySelection selection)
        {
            try
            {
                SetStatus("Slicing contours...");

                // Progress callback: updates Rhino status bar
                // Throttle UI updates to avoid reentrancy crashes
                int lastReported = -1;
                var lastUpdateTime = DateTime.Now;
                Action<double> reportProgress = (pct) =>
                {
                    int percent = (int)(pct * 100);
                    if (percent == lastReported) return;
                    lastReported = percent;
                    Rhino.UI.StatusBar.UpdateProgressMeter(percent, true);
                    Rhino.UI.StatusBar.SetMessagePane($"CCL_Clay3DP: {percent}%");

                    // Only call Wait() at most once per 500ms to avoid crashes
                    var now = DateTime.Now;
                    if ((now - lastUpdateTime).TotalMilliseconds > 500)
                    {
                        lastUpdateTime = now;
                        RhinoApp.Wait();
                    }
                };

                Rhino.UI.StatusBar.ShowProgressMeter(0, 100, "CCL_Clay3DP", true, true);

                // 2) Slice into contours
                List<Curve> contours;
                if (selection.Brep != null)
                {
                    contours = ContourSlicer.SliceBrep(
                        selection.Brep,
                        _settings.Helix.LayerHeight,
                        _settings.Height.HeightOffsetBottom,
                        _settings.Height.HeightOffsetTop,
                        reportProgress);
                }
                else
                {
                    contours = ContourSlicer.SliceMesh(
                        selection.Mesh,
                        _settings.Helix.LayerHeight,
                        _settings.Height.HeightOffsetBottom,
                        _settings.Height.HeightOffsetTop,
                        reportProgress);
                }

                SetStatus($"Found {contours.Count} contours. Interpolating spiral...");

                // 3) Generate spiral toolpath
                var spiralPoints = SpiralInterpolator.Interpolate(
                    contours,
                    _settings.Helix.FramesPerLayer,
                    _settings.Helix.StartAngle,
                    _settings.Helix.DirectionCCW,
                    reportProgress);

                reportProgress(0.1);
                SetStatus($"Computing {spiralPoints.Count} frames...");

                // 4) Compute frames
                var frames = FrameComputer.ComputeFrames(
                    spiralPoints,
                    selection.Brep,
                    selection.Mesh,
                    _settings.Ribbon.NormalOutward,
                    reportProgress);

                // 5) Create spiral curve
                Rhino.UI.StatusBar.UpdateProgressMeter(80, true);
                Rhino.UI.StatusBar.SetMessagePane("CCL_Clay3DP: 80% — Creating spiral curve...");
                RhinoApp.Wait();
                var spiralCurve = SpiralInterpolator.CreateSpiralCurve(spiralPoints);

                // 6) Generate ribbon mesh
                Rhino.UI.StatusBar.UpdateProgressMeter(90, true);
                Rhino.UI.StatusBar.SetMessagePane("CCL_Clay3DP: 90% — Generating ribbon...");
                RhinoApp.Wait();
                var ribbon = FrameComputer.GenerateRibbonMesh(frames, _settings.Ribbon.RibbonWidth);

                // 7) Prepare to bake
                Rhino.UI.StatusBar.UpdateProgressMeter(95, true);
                Rhino.UI.StatusBar.SetMessagePane("CCL_Clay3DP: 95% — Baking to document...");
                RhinoApp.Wait();

                // 7) Store result and geometry
                _lastGeometry = selection;
                _lastResult = new SpiralResult
                {
                    ToolpathPoints = spiralPoints,
                    Frames = frames,
                    SpiralCurve = spiralCurve,
                    RibbonMesh = ribbon,
                    Contours = contours,
                    LayerCount = contours.Count,
                };

                // 8) Bake to document
                BakeResults(_lastResult);

                Rhino.UI.StatusBar.UpdateProgressMeter(100, true);
                Rhino.UI.StatusBar.HideProgressMeter();
                SetStatus("Spiral slice complete");
                SetDetail(frames.Count, 0);
            }
            catch (Exception ex)
            {
                Rhino.UI.StatusBar.HideProgressMeter();
                SetStatus($"Error: {ex.Message}");
                RhinoApp.WriteLine($"[CCL_Clay3DP] Spiral slice failed: {ex}");
            }
        }

        private void BakeResults(SpiralResult result)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            // Ensure layers exist
            int contourLayer = EnsureLayer(doc, "3DP::Contours",
                System.Drawing.Color.FromArgb(100, 100, 100));
            int spiralLayer = EnsureLayer(doc, "3DP::Spiral Toolpath",
                System.Drawing.Color.FromArgb(255, 0, 0));
            int ribbonLayer = EnsureLayer(doc, "3DP::Ribbon",
                System.Drawing.Color.FromArgb(255, 255, 255));

            var attrs = new ObjectAttributes();

            // Bake contours
            foreach (var contour in result.Contours)
            {
                if (contour == null || !contour.IsValid) continue;
                attrs.LayerIndex = contourLayer;
                attrs.Name = "Contour";
                doc.Objects.AddCurve(contour, attrs);
            }

            // Bake spiral curve
            if (result.SpiralCurve != null && result.SpiralCurve.IsValid)
            {
                attrs.LayerIndex = spiralLayer;
                attrs.Name = "SpiralToolpath";
                doc.Objects.AddCurve(result.SpiralCurve, attrs);
            }

            // Bake ribbon
            if (result.RibbonMesh != null && result.RibbonMesh.IsValid)
            {
                attrs.LayerIndex = ribbonLayer;
                attrs.Name = "Ribbon";
                doc.Objects.AddMesh(result.RibbonMesh, attrs);
            }

            // Hide contour layer by default
            if (contourLayer >= 0 && contourLayer < doc.Layers.Count)
                doc.Layers[contourLayer].IsVisible = false;

            doc.Views.Redraw();
        }

        private static int EnsureLayer(RhinoDoc doc, string name, System.Drawing.Color color)
        {
            // Support nested layers with "::" separator
            string[] parts = name.Split(new[] { "::" }, StringSplitOptions.None);
            int parentIndex = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int found = -1;

                // Search for existing layer with this name and parent
                for (int li = 0; li < doc.Layers.Count; li++)
                {
                    var layer = doc.Layers[li];
                    if (layer.Name == part && layer.ParentLayerId ==
                        (parentIndex < 0 ? Guid.Empty : doc.Layers[parentIndex].Id))
                    {
                        found = li;
                        break;
                    }
                }

                if (found < 0)
                {
                    var newLayer = new Layer { Name = part, Color = color };
                    if (parentIndex >= 0)
                        newLayer.ParentLayerId = doc.Layers[parentIndex].Id;
                    found = doc.Layers.Add(newLayer);
                }

                parentIndex = found;
            }

            // Set color on the final layer
            if (parentIndex >= 0)
                doc.Layers[parentIndex].Color = color;

            return parentIndex;
        }

        private void OnAnalyzeClick(object sender, EventArgs e)
        {
            try
            {
                if (_lastResult == null || _lastResult.Frames.Count == 0)
                {
                    SetStatus("No spiral data. Run Spiral Slice first.");
                    return;
                }

                var doc = RhinoDoc.ActiveDoc;
                if (doc == null) return;

                // Parse channel from dropdown
                AnalysisChannel channel;
                switch (_analysisChannel.SelectedIndex)
                {
                    case 1: channel = AnalysisChannel.Robot; break;
                    case 2: channel = AnalysisChannel.Combined; break;
                    default: channel = AnalysisChannel.Clay; break;
                }

                SetStatus($"Analyzing ({channel})...");

                switch (channel)
                {
                    case AnalysisChannel.Clay:
                        // Direct mesh coloring — accurate, covers full surface
                        HeatmapDisplay.ShowOnGeometry(doc,
                            _lastGeometry?.Brep, _lastGeometry?.Mesh,
                            _settings.Clay);
                        break;

                    case AnalysisChannel.Robot:
                        // Frame-based — needs toolpath trajectory
                        HeatmapDisplay.ShowOnToolpath(doc,
                            _lastResult.ToolpathPoints, _lastResult.Frames,
                            _settings.Robot, _settings.Helix.FramesPerLayer);
                        break;

                    case AnalysisChannel.Combined:
                        // Both on geometry mesh
                        HeatmapDisplay.ShowCombined(doc,
                            _lastGeometry?.Brep, _lastGeometry?.Mesh,
                            _lastResult.ToolpathPoints, _lastResult.Frames,
                            _settings.Clay, _settings.Robot);
                        break;
                }

                SetStatus($"Analysis ({channel}) complete");
                SetDetail(_lastResult.Frames.Count, 0);
            }
            catch (Exception ex)
            {
                SetStatus($"Analysis error: {ex.Message}");
                RhinoApp.WriteLine($"[CCL_Clay3DP] Analysis failed: {ex}");
            }
        }

        private void OnSendToRoboDKClick(object sender, EventArgs e)
        {
            // Warn if a RoboDK session is already running. Each Send reloads
            // the station template from disk, discarding any manual edits the
            // user has made in RoboDK. Fail-safe: if we can't enumerate
            // processes, skip the check rather than block the send.
            if (IsRoboDKRunning() && !ConfirmReplaceRoboDKSession())
            {
                SetStatus("Send to RoboDK cancelled");
                return;
            }
            PerformSendToRoboDK();
        }

        private void PerformSendToRoboDK()
        {
            try
            {
                if (_lastResult == null || _lastResult.Frames.Count == 0)
                {
                    SetStatus("No spiral data. Run Spiral Slice first.");
                    return;
                }

                SetStatus($"Serializing {_lastResult.Frames.Count} frames...");

                string jsonPath = RoboDK.FrameSerializer.SerializeToFile(
                    _lastResult.Frames, _settings.Robot, _settings.Helix.LayerHeight);

                SetStatus("Sending to RoboDK...");
                RhinoApp.Wait();

                RoboDK.RoboDKSubprocess.SendFrames(jsonPath, _settings.Robot);

                // Clean up temp file
                try { System.IO.File.Delete(jsonPath); } catch { }

                SetStatus($"Sent {_lastResult.Frames.Count} targets to RoboDK");
                _hasSentToRoboDK = true;
            }
            catch (Exception ex)
            {
                SetStatus($"RoboDK error: {ex.Message}");
                RhinoApp.WriteLine($"[CCL_Clay3DP] RoboDK failed: {ex}");
            }
        }

        // Tracks whether the user has already sent to RoboDK in this session.
        // Used to decide whether a settings-triggered auto-regenerate should
        // also auto-push the updated toolpath to RoboDK — we only do that if
        // RoboDK is already in the workflow; otherwise an auto-send would be
        // surprising (it would spawn RoboDK out of the blue).
        private bool _hasSentToRoboDK = false;

        private static bool IsRoboDKRunning()
        {
            try
            {
                return System.Diagnostics.Process
                    .GetProcessesByName("RoboDK").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool ConfirmReplaceRoboDKSession()
        {
            var result = MessageBox.Show(
                this,
                "RoboDK appears to already be running.\n\n" +
                "Sending will reload the station template from disk, which " +
                "discards any manual changes you may have made in RoboDK " +
                "(renamed items, edited targets, new programs, etc.).\n\n" +
                "Continue and replace the current RoboDK session?",
                "CCL_Clay3DP — Replace RoboDK session?",
                MessageBoxButtons.YesNo,
                MessageBoxType.Warning,
                MessageBoxDefaultButton.No);
            return result == DialogResult.Yes;
        }

        private void OnRunAllClick(object sender, EventArgs e)
        {
            OnSpiralSliceClick(sender, e);
            if (_lastResult == null || _lastResult.Frames.Count == 0) return;

            OnAnalyzeClick(sender, e);
            OnSendToRoboDKClick(sender, e);
        }

        private void SetStatus(string message)
        {
            _statusLabel.Text = $"Status: {message}";
            RhinoApp.WriteLine($"[CCL_Clay3DP] {message}");
        }

        private void SetDetail(int frames, int issues)
        {
            _detailLabel.Text = $"Frames: {frames}  |  Issues: {issues}";
        }
    }

    internal class Divider : Panel
    {
        public Divider()
        {
            Height = 1;
            BackgroundColor = Colors.Gray;
        }
    }
}
