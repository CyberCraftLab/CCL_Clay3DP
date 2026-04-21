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
using CCL_Clay3DP.Zigzag;

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

            var shellButton = new Button { Text = "Zigzag Test (experimental)" };
            shellButton.Click += OnZigzagTestClick;

            var pipesButton = new Button { Text = "Pipes (visualization)" };
            pipesButton.Click += OnPipesClick;

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
                    new TableRow(shellButton),
                    new TableRow(pipesButton),
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
            "3DP::Zigzag Contour",
            "3DP::Zigzag Outer Pts",
            "3DP::Zigzag Inner Pts",
            "3DP::Zigzag Inner Curves",
            "3DP::Zigzag",
            "3DP::Zigzag Inward Vectors",
            "3DP::Zigzag Pipes",
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

        private void OnZigzagTestClick(object sender, EventArgs e)
        {
            try
            {
                SetStatus("Zigzag: select geometry...");
                var selection = GeometrySelector.Select();
                if (selection == null) { SetStatus("Cancelled"); return; }

                // Non-per-run params come from settings.
                int numPoints = _settings.Zigzag.NumPoints;
                double layerHeight = _settings.Helix.LayerHeight;

                // Slice now so we can preview inward arrows before asking the
                // user to pick flip direction and offset distance.
                SetStatus($"Zigzag: slicing at {layerHeight}mm layer height...");
                RhinoApp.Wait();
                List<Curve> contours = selection.Brep != null
                    ? ContourSlicer.SliceBrep(selection.Brep, layerHeight, 0, 0)
                    : ContourSlicer.SliceMesh(selection.Mesh, layerHeight, 0, 0);

                // Preview arrows: fixed short length (5 mm) just to show the
                // algorithm's default inward-direction choice. User answers
                // Flip based on what they see, then picks distance.
                const double previewArrowLength = 5.0;
                BakePreviewArrows(contours, numPoints, previewArrowLength, false);
                RhinoApp.Wait();

                bool flipInward = false;
                var gbFlip = Rhino.Input.RhinoGet.GetBool(
                    "Flip inward direction? (red arrows in viewport show current side)",
                    true, "No", "Yes", ref flipInward);
                if (gbFlip != Rhino.Commands.Result.Success &&
                    gbFlip != Rhino.Commands.Result.Nothing)
                {
                    SetStatus("Cancelled");
                    return;
                }

                if (flipInward)
                {
                    ClearLayerObjects(RhinoDoc.ActiveDoc, "3DP::Zigzag Inward Vectors");
                    BakePreviewArrows(contours, numPoints, previewArrowLength, true);
                    RhinoApp.Wait();
                }

                double distance = 3.0;
                var gnD = new Rhino.Input.Custom.GetNumber();
                gnD.SetCommandPrompt("Inward offset distance (mm)");
                gnD.SetDefaultNumber(distance);
                gnD.SetLowerLimit(0.01, false);
                if (gnD.Get() != Rhino.Input.GetResult.Number)
                {
                    SetStatus("Cancelled");
                    return;
                }
                distance = gnD.Number();

                // Clear preview arrows; BakeZigzagStack will re-bake them at
                // the actual chosen distance so the final visualization reflects
                // the real inward offset magnitude.
                ClearLayerObjects(RhinoDoc.ActiveDoc, "3DP::Zigzag Inward Vectors");

                SetStatus($"Zigzag: generating {contours.Count} layers...");
                RhinoApp.Wait();

                var goodContours = new List<Curve>();
                var results = new List<Zigzag.SimpleZigzagResult>();
                int skipped = 0;
                for (int i = 0; i < contours.Count; i++)
                {
                    try
                    {
                        var r = Zigzag.ZigzagGenerator.BuildSingleContour(
                            contours[i], numPoints, distance, flipInward);
                        results.Add(r);
                        goodContours.Add(contours[i]);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        RhinoApp.WriteLine(
                            $"[CCL_Clay3DP] Zigzag layer {i} skipped: {ex.Message}");
                    }
                }

                BakeZigzagStack(goodContours, results);
                SetStatus(
                    $"Zigzag complete: {results.Count} layers" +
                    (skipped > 0 ? $", {skipped} skipped" : ""));
            }
            catch (Exception ex)
            {
                SetStatus($"Zigzag error: {ex.Message}");
                RhinoApp.WriteLine($"[CCL_Clay3DP] Zigzag failed: {ex}");
            }
        }

        private void BakeZigzagStack(
            List<Curve> contours, List<Zigzag.SimpleZigzagResult> results)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            int contourLayer = EnsureLayer(doc, "3DP::Zigzag Contour",
                System.Drawing.Color.FromArgb(180, 180, 180));
            int outerPtsLayer = EnsureLayer(doc, "3DP::Zigzag Outer Pts",
                System.Drawing.Color.FromArgb(40, 40, 40));
            int innerPtsLayer = EnsureLayer(doc, "3DP::Zigzag Inner Pts",
                System.Drawing.Color.FromArgb(40, 80, 200));
            int innerCurveLayer = EnsureLayer(doc, "3DP::Zigzag Inner Curves",
                System.Drawing.Color.FromArgb(80, 120, 220));
            int zzLayer = EnsureLayer(doc, "3DP::Zigzag",
                System.Drawing.Color.FromArgb(0, 200, 255));
            int arrowLayer = EnsureLayer(doc, "3DP::Zigzag Inward Vectors",
                System.Drawing.Color.FromArgb(220, 40, 40));

            var attrs = new ObjectAttributes();

            for (int i = 0; i < contours.Count; i++)
            {
                var contour = contours[i];
                var r = results[i];

                if (contour != null && contour.IsValid)
                {
                    attrs.LayerIndex = contourLayer;
                    attrs.Name = "ZigzagContour";
                    doc.Objects.AddCurve(contour, attrs);
                }

                attrs.LayerIndex = outerPtsLayer;
                attrs.Name = "ZigzagOuterPt";
                foreach (var p in r.OuterPoints) doc.Objects.AddPoint(p, attrs);

                attrs.LayerIndex = innerPtsLayer;
                attrs.Name = "ZigzagInnerPt";
                foreach (var p in r.InnerPoints) doc.Objects.AddPoint(p, attrs);

                if (r.InnerCurve != null && r.InnerCurve.IsValid)
                {
                    attrs.LayerIndex = innerCurveLayer;
                    attrs.Name = "ZigzagInnerCurve";
                    doc.Objects.AddCurve(r.InnerCurve, attrs);
                }

                // Inward-direction arrows: one line per outer point, with
                // EndArrowhead decoration so Rhino renders a real arrow
                // pointing at the corresponding inner point.
                var arrowAttrs = new ObjectAttributes
                {
                    LayerIndex = arrowLayer,
                    Name = "ZigzagInwardArrow",
                    ObjectDecoration = ObjectDecoration.EndArrowhead,
                };
                int arrowCount = Math.Min(r.OuterPoints.Count, r.InnerPoints.Count);
                for (int k = 0; k < arrowCount; k++)
                {
                    var line = new LineCurve(r.OuterPoints[k], r.InnerPoints[k]);
                    if (line.IsValid)
                        doc.Objects.AddCurve(line, arrowAttrs);
                }

                if (r.Zigzag != null && r.Zigzag.IsValid)
                {
                    attrs.LayerIndex = zzLayer;
                    attrs.Name = "Zigzag";
                    doc.Objects.AddCurve(r.Zigzag, attrs);
                }
            }

            // Hide the point and contour helper layers by default so the
            // zigzag curves read clean; user can toggle them on if needed.
            // Arrows also hidden by default — enable to verify the inward
            // direction choice before the next run.
            if (contourLayer >= 0 && contourLayer < doc.Layers.Count)
                doc.Layers[contourLayer].IsVisible = false;
            if (outerPtsLayer >= 0 && outerPtsLayer < doc.Layers.Count)
                doc.Layers[outerPtsLayer].IsVisible = false;
            if (innerPtsLayer >= 0 && innerPtsLayer < doc.Layers.Count)
                doc.Layers[innerPtsLayer].IsVisible = false;
            // Keep arrow layer visible — user asked to see the inward direction.
            if (arrowLayer >= 0 && arrowLayer < doc.Layers.Count)
                doc.Layers[arrowLayer].IsVisible = true;

            doc.Views.Redraw();
        }

        private void OnPipesClick(object sender, EventArgs e)
        {
            try
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null) { SetStatus("No active document"); return; }

                // Bead diameter comes from clay settings — not prompted.
                double diameter = _settings.Clay.BeadDiameter;
                if (diameter <= 0)
                {
                    SetStatus("Bead diameter must be > 0 in Clay settings");
                    return;
                }
                double radius = diameter * 0.5;

                // Source layers: outer contours, inner curves, zigzags
                var sourceLayerNames = new[]
                {
                    "3DP::Zigzag Contour",
                    "3DP::Zigzag Inner Curves",
                    "3DP::Zigzag",
                };
                var srcIndices = new System.Collections.Generic.HashSet<int>();
                foreach (var name in sourceLayerNames)
                {
                    int idx = doc.Layers.FindByFullPath(name, -1);
                    if (idx >= 0) srcIndices.Add(idx);
                }
                if (srcIndices.Count == 0)
                {
                    SetStatus("No zigzag curves found — run Zigzag first");
                    return;
                }

                var curves = new List<Curve>();
                foreach (var obj in doc.Objects)
                {
                    if (obj == null || obj.IsDeleted) continue;
                    if (!srcIndices.Contains(obj.Attributes.LayerIndex)) continue;
                    if (obj.Geometry is Curve c) curves.Add(c);
                }

                if (curves.Count == 0)
                {
                    SetStatus("No curves on zigzag layers");
                    return;
                }

                // Warn before starting — piping can take a while on dense stacks
                // and users were getting spooked by the hang.
                var confirm = MessageBox.Show(
                    this,
                    $"About to pipe {curves.Count} curves at {diameter:F2} mm " +
                    "bead diameter.\n\nThis can take a few moments on dense " +
                    "geometry. Continue?",
                    "CCL_Clay3DP — Generate pipes",
                    MessageBoxButtons.YesNo,
                    MessageBoxType.Information,
                    MessageBoxDefaultButton.Yes);
                if (confirm != DialogResult.Yes)
                {
                    SetStatus("Pipes cancelled");
                    return;
                }

                int pipesLayer = EnsureLayer(doc, "3DP::Zigzag Pipes",
                    System.Drawing.Color.FromArgb(220, 180, 80));

                var attrs = new ObjectAttributes
                {
                    LayerIndex = pipesLayer,
                    Name = "ZigzagPipe",
                };

                Rhino.UI.StatusBar.ShowProgressMeter(
                    0, curves.Count, "CCL_Clay3DP: piping", true, true);
                var lastUpdate = DateTime.Now;

                int pipedOk = 0, failed = 0;
                for (int i = 0; i < curves.Count; i++)
                {
                    var c = curves[i];

                    // Ensure a single continuous input curve. PolyCurves from
                    // JoinCurves can pipe as many fragments — unnest first so
                    // the pipe treats it as one sweep.
                    if (c is PolyCurve pc)
                    {
                        pc = (PolyCurve)pc.DuplicateCurve();
                        pc.RemoveNesting();
                        c = pc;
                    }

                    Mesh pipe = null;
                    try
                    {
                        pipe = Mesh.CreateFromCurvePipe(
                            c, radius, 12, 1,
                            MeshPipeCapStyle.Flat, false);
                    }
                    catch { }

                    if (pipe != null && pipe.IsValid)
                    {
                        doc.Objects.AddMesh(pipe, attrs);
                        pipedOk++;
                    }
                    else
                    {
                        failed++;
                    }

                    // Mesh spheres at each polyline vertex — fills the kink
                    // at every bend so the viz reads as one continuous bead.
                    // Skipped for non-polyline (NURBS) curves which don't have
                    // discrete bends.
                    if (c.TryGetPolyline(out Polyline pl) && pl.Count > 0)
                    {
                        // Closed polylines repeat the first vertex at the end;
                        // skip the duplicate to avoid a double sphere at the seam.
                        int last = pl.IsClosed ? pl.Count - 1 : pl.Count;
                        for (int k = 0; k < last; k++)
                        {
                            var sphereMesh = Mesh.CreateFromSphere(
                                new Sphere(pl[k], radius), 12, 8);
                            if (sphereMesh != null && sphereMesh.IsValid)
                                doc.Objects.AddMesh(sphereMesh, attrs);
                        }
                    }

                    Rhino.UI.StatusBar.UpdateProgressMeter(i + 1, true);
                    var now = DateTime.Now;
                    if ((now - lastUpdate).TotalMilliseconds > 500)
                    {
                        lastUpdate = now;
                        Rhino.UI.StatusBar.SetMessagePane(
                            $"CCL_Clay3DP: piping {i + 1}/{curves.Count}");
                        RhinoApp.Wait();
                    }
                }
                Rhino.UI.StatusBar.HideProgressMeter();

                doc.Views.Redraw();
                SetStatus(
                    $"Pipes: {pipedOk}/{curves.Count} meshes" +
                    (failed > 0 ? $" ({failed} failed)" : ""));
            }
            catch (Exception ex)
            {
                Rhino.UI.StatusBar.HideProgressMeter();
                SetStatus($"Pipes error: {ex.Message}");
                RhinoApp.WriteLine($"[CCL_Clay3DP] Pipes failed: {ex}");
            }
        }

        /// <summary>
        /// Bake inward-direction arrows at a fixed viz length for each layer's
        /// outer points. Called before the flip/distance prompts so the user
        /// can see which side the algorithm picked as inward.
        /// </summary>
        private void BakePreviewArrows(
            List<Curve> contours, int numPoints, double length, bool flip)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            int arrowLayer = EnsureLayer(doc, "3DP::Zigzag Inward Vectors",
                System.Drawing.Color.FromArgb(220, 40, 40));
            if (arrowLayer >= 0 && arrowLayer < doc.Layers.Count)
                doc.Layers[arrowLayer].IsVisible = true;

            var attrs = new ObjectAttributes
            {
                LayerIndex = arrowLayer,
                Name = "ZigzagInwardArrowPreview",
                ObjectDecoration = ObjectDecoration.EndArrowhead,
            };

            foreach (var contour in contours)
            {
                if (contour == null) continue;
                try
                {
                    var r = Zigzag.ZigzagGenerator.BuildSingleContour(
                        contour, numPoints, length, flip);
                    int n = Math.Min(r.OuterPoints.Count, r.InnerPoints.Count);
                    for (int k = 0; k < n; k++)
                    {
                        var line = new LineCurve(r.OuterPoints[k], r.InnerPoints[k]);
                        if (line.IsValid) doc.Objects.AddCurve(line, attrs);
                    }
                }
                catch { /* skip bad layers silently for preview */ }
            }

            doc.Views.Redraw();
        }

        /// <summary>
        /// Delete all objects on a specific Rhino layer (by full "::" path).
        /// Used to clear preview arrows before re-baking.
        /// </summary>
        private void ClearLayerObjects(RhinoDoc doc, string fullPath)
        {
            if (doc == null) return;
            int idx = doc.Layers.FindByFullPath(fullPath, -1);
            if (idx < 0) return;
            var victims = new List<Rhino.DocObjects.RhinoObject>();
            foreach (var obj in doc.Objects)
            {
                if (obj == null || obj.IsDeleted) continue;
                if (obj.Attributes.LayerIndex == idx) victims.Add(obj);
            }
            foreach (var v in victims) doc.Objects.Delete(v, true);
            doc.Views.Redraw();
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
