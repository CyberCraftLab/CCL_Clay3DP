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

        // Workflow controls disabled until the user reviews Settings at least
        // once in this session. Avoids users clicking Slice with stale or
        // unconfigured params.
        private readonly List<Control> _gatedControls = new List<Control>();
        private bool _settingsReviewed = false;

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
            var sliceButton = new Button { Text = "1. Slice" };
            sliceButton.Click += OnSliceClick;

            var previewClayModelButton = new Button { Text = "Preview Clay Model" };
            previewClayModelButton.Click += OnPreviewClayModelClick;

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

            // Gate the workflow — disabled until user reviews Settings once.
            _gatedControls.Add(sliceButton);
            _gatedControls.Add(_analysisChannel);
            _gatedControls.Add(analyzeButton);
            _gatedControls.Add(sendButton);
            _gatedControls.Add(runAllButton);
            _gatedControls.Add(previewClayModelButton);
            foreach (var c in _gatedControls) c.Enabled = false;

            // Status
            _statusLabel = new Label { Text = "Status: Open Settings to start" };
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
                    new TableRow(previewClayModelButton),
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
            "3DP::Outer Toolpath",
            "3DP::Bracing Outer Points",
            "3DP::Bracing Inner Points",
            "3DP::Inner Toolpath",
            "3DP::Bracing Toolpath",
            "3DP::Bracing Vectors",
            "3DP::Clay Model",
        };

        private void OnSettingsClick(object sender, EventArgs e)
        {
            var dialog = new SettingsDialog(_settings);
            if (!dialog.ShowModal(this)) return;

            _settings = SettingsManager.Load();

            // First successful review unlocks the rest of the workflow.
            if (!_settingsReviewed)
            {
                _settingsReviewed = true;
                foreach (var c in _gatedControls) c.Enabled = true;
            }

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

            if (geometryToRebuild != null && _settings.Helix.SpiralSlice)
            {
                // Auto-rebuild only makes sense for Spiral Slice — the Layer
                // Slice flow can be interactive (flip + distance prompts when
                // Inner Wall Bracing is on), so we leave it for the user to
                // kick off with the Slice button.
                SetStatus("Settings changed — regenerating spiral...");
                RhinoApp.Wait();
                RunSliceAndBake(geometryToRebuild);

                // If the user has already pushed this job to RoboDK in this
                // session, keep RoboDK in sync with the new settings without
                // making them click Send again. Skip the confirm dialog
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
                SetStatus("Generated layers cleared — click Slice to regenerate");
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
            // Rhino refuses to delete a layer that is the "current" drawing
            // layer. If any generated layer happens to be current, bump the
            // current layer to the first non-plugin layer first.
            try
            {
                int cur = doc.Layers.CurrentLayerIndex;
                if (cur >= 0 && cur < doc.Layers.Count)
                {
                    var curLayer = doc.Layers[cur];
                    if (curLayer != null && !curLayer.IsDeleted
                        && IsGeneratedLayer(curLayer.FullPath))
                    {
                        for (int i = 0; i < doc.Layers.Count; i++)
                        {
                            var l = doc.Layers[i];
                            if (l == null || l.IsDeleted) continue;
                            if (!IsGeneratedLayer(l.FullPath)
                                && l.ParentLayerId == Guid.Empty)
                            {
                                doc.Layers.SetCurrentLayerIndex(i, true);
                                break;
                            }
                        }
                    }
                }
            }
            catch { /* best-effort; continue */ }

            int objsDeleted = 0, objsFailed = 0, layersDeleted = 0;
            var undeletable = new List<string>();

            foreach (var layerName in GeneratedLayerNames)
            {
                int idx = doc.Layers.FindByFullPath(layerName, -1);
                if (idx < 0) continue;

                var layer = doc.Layers[idx];
                if (layer == null || layer.IsDeleted) continue;

                // Normalize the layer state so Delete() isn't blocked by
                // locked/hidden flags.
                if (layer.IsLocked) layer.IsLocked = false;
                if (!layer.IsVisible) layer.IsVisible = true;

                // Delete objects on this layer (snapshot first — mutating
                // doc.Objects while enumerating is unsafe).
                var victims = new List<Rhino.DocObjects.RhinoObject>();
                foreach (var obj in doc.Objects)
                {
                    if (obj == null || obj.IsDeleted) continue;
                    if (obj.Attributes.LayerIndex == idx) victims.Add(obj);
                }
                foreach (var v in victims)
                {
                    if (doc.Objects.Delete(v, true)) objsDeleted++;
                    else objsFailed++;
                }

                if (doc.Layers.Delete(idx, true))
                    layersDeleted++;
                else
                    undeletable.Add(layerName);
            }

            doc.Views.Redraw();
            string msg = $"[CCL_Clay3DP] Cleared {objsDeleted} object(s), " +
                         $"{layersDeleted} layer(s)";
            if (objsFailed > 0) msg += $"; {objsFailed} object(s) could not be deleted";
            if (undeletable.Count > 0)
                msg += $"; could not delete layers: {string.Join(", ", undeletable)}";
            RhinoApp.WriteLine(msg);
        }

        private static bool IsGeneratedLayer(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            foreach (var name in GeneratedLayerNames)
                if (fullPath == name) return true;
            return false;
        }

        private void OnSliceClick(object sender, EventArgs e)
        {
            SetStatus("Selecting geometry...");
            var selection = GeometrySelector.Select();
            if (selection == null)
            {
                SetStatus("Cancelled");
                return;
            }

            // Clean slate: remove any generated output from a previous run
            // (including output from the OTHER mode) so what's in Rhino
            // always reflects the current Settings choice.
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
            {
                ClearGeneratedContent(doc);
                _lastResult = null;
            }

            if (_settings.Helix.SpiralSlice)
                RunSliceAndBake(selection);
            else
                RunLayerSlice(selection);
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

            // Contours are intermediate data used to compute the spiral; we
            // no longer bake them — the user doesn't need them in the layer
            // panel. The SpiralCurve is the actual toolpath output.
            int spiralLayer = EnsureLayer(doc, "3DP::Spiral Toolpath",
                System.Drawing.Color.FromArgb(255, 0, 0));
            int ribbonLayer = EnsureLayer(doc, "3DP::Ribbon",
                System.Drawing.Color.FromArgb(255, 255, 255));

            var attrs = new ObjectAttributes();

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

            // Ribbon layer is off by default — it's a debug visual that the
            // user can toggle on if they want to inspect tool orientation.
            if (ribbonLayer >= 0 && ribbonLayer < doc.Layers.Count)
                doc.Layers[ribbonLayer].IsVisible = false;

            doc.Views.Redraw();
        }

        /// <summary>
        /// Create (or update) a PBR Rhino render material that matches the
        /// active clay preset, then assign it to the given layer. The material
        /// is named "CCL Clay - {PresetName}" so it's idempotent across runs:
        /// repeated calls update the existing material in place rather than
        /// piling up duplicates in the document Materials table.
        ///
        /// Sets both legacy (DiffuseColor) and PBR (BaseColor / Roughness /
        /// Subsurface) properties so the layer reads correctly in any of
        /// Rhino 8's display modes — Wireframe, Shaded, and Rendered.
        /// </summary>
        private static void EnsureClayRenderMaterial(
            RhinoDoc doc, int layerIndex, string presetName,
            ClayPreviewMaterials.ClayPbr pbr)
        {
            if (doc == null || pbr == null) return;
            if (layerIndex < 0 || layerIndex >= doc.Layers.Count) return;

            string matName = $"CCL Clay - {presetName ?? "Stoneware"}";

            // Find existing material by name (case-sensitive, exact match) or
            // create a new one. Material.Find returns -1 if not found.
            int matIdx = doc.Materials.Find(matName, true);
            Rhino.DocObjects.Material mat;
            if (matIdx < 0)
            {
                mat = new Rhino.DocObjects.Material { Name = matName };
                matIdx = doc.Materials.Add(mat);
                mat = doc.Materials[matIdx];
            }
            else
            {
                mat = doc.Materials[matIdx];
            }

            // Legacy / non-PBR fallback values — used by Shaded display and
            // any renderer that doesn't honor the PBR aspect.
            mat.DiffuseColor = pbr.BaseColor;
            mat.SpecularColor = System.Drawing.Color.FromArgb(40, 40, 40);
            mat.Shine = 0.05 * Rhino.DocObjects.Material.MaxShine; // very matte

            // PBR aspect — ensure the material is in PBR mode then write the
            // physically-based fields. Read in Rendered viewport.
            mat.ToPhysicallyBased();
            var pbrMat = mat.PhysicallyBased;
            if (pbrMat != null)
            {
                pbrMat.BaseColor = new Rhino.Display.Color4f(pbr.BaseColor);
                pbrMat.Roughness = pbr.Roughness;
                pbrMat.Metallic = 0.0;
                pbrMat.Subsurface = pbr.Subsurface;
                pbrMat.SubsurfaceScatteringColor =
                    new Rhino.Display.Color4f(pbr.SubsurfaceColor);
            }
            mat.CommitChanges();

            // Assign to layer's render material slot. Layer changes are
            // immediate in Rhino 8 — no CommitChanges call required.
            var layer = doc.Layers[layerIndex];
            if (layer != null && !layer.IsDeleted)
                layer.RenderMaterialIndex = matIdx;
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
                    SetStatus("No slice data. Run Slice first.");
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
                        // Mesh-based — color the input geometry mesh by the
                        // robot score at the nearest toolpath point, so Clay
                        // and Robot heatmaps share the same visual target.
                        HeatmapDisplay.ShowRobotOnGeometry(doc,
                            _lastGeometry?.Brep, _lastGeometry?.Mesh,
                            _lastResult.ToolpathPoints, _lastResult.Frames,
                            _settings.Robot);
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
                    SetStatus("No slice data. Run Slice first.");
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

        private void RunLayerSlice(GeometrySelection selection)
        {
            try
            {
                double layerHeight = _settings.Helix.LayerHeight;
                bool bracing = _settings.Helix.InnerWallBracing;

                SetStatus($"Layer slice: slicing at {layerHeight} mm...");
                RhinoApp.Wait();
                List<Curve> contours = selection.Brep != null
                    ? ContourSlicer.SliceBrep(
                        selection.Brep, layerHeight,
                        _settings.Height.HeightOffsetBottom,
                        _settings.Height.HeightOffsetTop)
                    : ContourSlicer.SliceMesh(
                        selection.Mesh, layerHeight,
                        _settings.Height.HeightOffsetBottom,
                        _settings.Height.HeightOffsetTop);

                if (!bracing)
                {
                    // Simple layer slice — just the outer contours.
                    BakeLayerContours(contours);

                    // Cache for Analyze: sample outer contours + build +Z-up
                    // frames so Clay / Robot / Both all work in this mode.
                    var pts = new List<Point3d>();
                    foreach (var c in contours) AddCurvePoints(c, pts);
                    _lastGeometry = selection;
                    _lastResult = new SpiralResult
                    {
                        ToolpathPoints = pts,
                        Frames = ComputeLayerFrames(pts),
                        Contours = contours,
                        LayerCount = contours.Count,
                    };

                    SetStatus($"Layer slice complete: {contours.Count} contours");
                    return;
                }

                // Inner Wall Bracing: preview inward arrows so the user can
                // flip the side before picking the offset distance.
                int numPoints = _settings.Helix.FramesPerLayer;
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
                    ClearLayerObjects(RhinoDoc.ActiveDoc, "3DP::Bracing Vectors");
                    BakePreviewArrows(contours, numPoints, previewArrowLength, true);
                    RhinoApp.Wait();
                }

                double distance = 10.0;
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

                // Clear preview arrows; BakeZigzagStack re-bakes them at the
                // real distance so the viz reflects the final offset.
                ClearLayerObjects(RhinoDoc.ActiveDoc, "3DP::Bracing Vectors");

                SetStatus($"Layer slice + bracing: generating {contours.Count} layers...");
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
                            $"[CCL_Clay3DP] Layer {i} bracing skipped: {ex.Message}");
                    }
                }

                if (goodContours.Count == 0)
                {
                    SetStatus(
                        $"Layer slice + bracing: no layers produced " +
                        $"({skipped} skipped). Check bracing offset vs. geometry.");
                    return;
                }

                BakeZigzagStack(goodContours, results, flipInward);

                // Robot print order per layer (bottom to top):
                //   Inner Toolpath → Outer Toolpath → Bracing Toolpath
                // The flip swaps which underlying curve ends up on which
                // named layer, so the print order follows suit.
                var layerPts = new List<Point3d>();
                for (int i = 0; i < goodContours.Count; i++)
                {
                    var innerLayerCurve = flipInward
                        ? goodContours[i]
                        : results[i].InnerCurve;
                    var outerLayerCurve = flipInward
                        ? results[i].InnerCurve
                        : goodContours[i];

                    if (innerLayerCurve != null)
                        AddCurvePoints(innerLayerCurve, layerPts);
                    if (outerLayerCurve != null)
                        AddCurvePoints(outerLayerCurve, layerPts);
                    if (results[i].Zigzag != null)
                        AddCurvePoints(results[i].Zigzag, layerPts);
                }

                _lastGeometry = selection;
                _lastResult = new SpiralResult
                {
                    ToolpathPoints = layerPts,
                    Frames = ComputeLayerFrames(layerPts),
                    Contours = goodContours,
                    LayerCount = goodContours.Count,
                };

                SetStatus(
                    $"Layer slice + bracing complete: {results.Count} layers" +
                    (skipped > 0 ? $", {skipped} skipped" : ""));
            }
            catch (Exception ex)
            {
                SetStatus($"Layer slice error: {ex.Message}");
                RhinoApp.WriteLine($"[CCL_Clay3DP] Layer slice failed: {ex}");
            }
        }

        /// <summary>
        /// Bake outer contours only (Layer Slice without bracing). Uses the
        /// Outer Toolpath layer so Preview Clay Model can pick them up uniformly.
        /// </summary>
        private void BakeLayerContours(List<Curve> contours)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            int contourLayer = EnsureLayer(doc, "3DP::Outer Toolpath",
                System.Drawing.Color.FromArgb(180, 180, 180));
            var attrs = new ObjectAttributes
            {
                LayerIndex = contourLayer,
                Name = "LayerContour",
            };
            foreach (var c in contours)
            {
                if (c == null || !c.IsValid) continue;
                doc.Objects.AddCurve(c, attrs);
            }
            // Unlike the bracing bake, keep the contour layer visible — it
            // is the only output of this mode.
            if (contourLayer >= 0 && contourLayer < doc.Layers.Count)
                doc.Layers[contourLayer].IsVisible = true;
            doc.Views.Redraw();
        }

        /// <summary>
        /// Pull points from a curve for Layer-mode Analyze caching. Polylines
        /// contribute their vertices directly; smooth curves are sampled at
        /// ~2 mm spacing. Duplicate closing vertex on closed polylines is
        /// skipped so frame tangents at the seam don't degenerate.
        /// </summary>
        private static void AddCurvePoints(Curve c, List<Point3d> acc)
        {
            if (c == null) return;
            if (c.TryGetPolyline(out Polyline pl) && pl.Count > 0)
            {
                int count = pl.IsClosed ? pl.Count - 1 : pl.Count;
                for (int i = 0; i < count; i++) acc.Add(pl[i]);
                return;
            }
            double len = c.GetLength();
            int n = Math.Max(32, (int)(len / 2.0));
            var ts = c.DivideByCount(n, false);
            if (ts == null) return;
            foreach (var t in ts) acc.Add(c.PointAt(t));
        }

        /// <summary>
        /// Build tool frames for Layer-mode toolpaths: Z is always world +Z
        /// (tool points straight up), X is the horizontal tangent, Y fills
        /// the frame orthogonally. Used by the Robot / Both Analyze channels.
        /// </summary>
        private static List<Plane> ComputeLayerFrames(List<Point3d> pts)
        {
            var frames = new List<Plane>(pts.Count);
            if (pts.Count < 2) return frames;

            for (int i = 0; i < pts.Count; i++)
            {
                Vector3d tangent;
                if (i == 0) tangent = pts[1] - pts[0];
                else if (i == pts.Count - 1) tangent = pts[i] - pts[i - 1];
                else tangent = pts[i + 1] - pts[i - 1];

                tangent.Z = 0;
                if (!tangent.Unitize())
                {
                    // Degenerate — fall back to a world-aligned frame at this point.
                    var fallback = Plane.WorldXY;
                    fallback.Origin = pts[i];
                    frames.Add(fallback);
                    continue;
                }

                // Y = 90° CCW rotation of tangent in the XY plane. Z of the
                // resulting Plane(origin, X, Y) is +Z by construction.
                var yAxis = new Vector3d(-tangent.Y, tangent.X, 0);
                frames.Add(new Plane(pts[i], tangent, yAxis));
            }
            return frames;
        }

        private void BakeZigzagStack(
            List<Curve> contours,
            List<Zigzag.SimpleZigzagResult> results,
            bool flipInward)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            int outerToolpathLayer = EnsureLayer(doc, "3DP::Outer Toolpath",
                System.Drawing.Color.FromArgb(180, 180, 180));
            int innerToolpathLayer = EnsureLayer(doc, "3DP::Inner Toolpath",
                System.Drawing.Color.FromArgb(80, 120, 220));
            int outerPtsLayer = EnsureLayer(doc, "3DP::Bracing Outer Points",
                System.Drawing.Color.FromArgb(40, 40, 40));
            int innerPtsLayer = EnsureLayer(doc, "3DP::Bracing Inner Points",
                System.Drawing.Color.FromArgb(40, 80, 200));
            int zzLayer = EnsureLayer(doc, "3DP::Bracing Toolpath",
                System.Drawing.Color.FromArgb(0, 200, 255));
            int arrowLayer = EnsureLayer(doc, "3DP::Bracing Vectors",
                System.Drawing.Color.FromArgb(220, 40, 40));

            // When flipped, the projected curve is geometrically OUTSIDE the
            // original slice. Swap the layer/name assignments for both curves
            // and their sample points so "Outer Toolpath" always holds the
            // geometrically outer data and "Inner Toolpath" the inner data.
            int sliceCurveTarget = flipInward ? innerToolpathLayer : outerToolpathLayer;
            int projectedCurveTarget = flipInward ? outerToolpathLayer : innerToolpathLayer;
            int slicePointsTarget = flipInward ? innerPtsLayer : outerPtsLayer;
            int projectedPointsTarget = flipInward ? outerPtsLayer : innerPtsLayer;
            string sliceCurveName = flipInward ? "InnerToolpath" : "OuterToolpath";
            string projectedCurveName = flipInward ? "OuterToolpath" : "InnerToolpath";
            string slicePointsName = flipInward ? "BracingInnerPoint" : "BracingOuterPoint";
            string projectedPointsName = flipInward ? "BracingOuterPoint" : "BracingInnerPoint";

            var attrs = new ObjectAttributes();

            for (int i = 0; i < contours.Count; i++)
            {
                var contour = contours[i];
                var r = results[i];

                if (contour != null && contour.IsValid)
                {
                    attrs.LayerIndex = sliceCurveTarget;
                    attrs.Name = sliceCurveName;
                    doc.Objects.AddCurve(contour, attrs);
                }

                attrs.LayerIndex = slicePointsTarget;
                attrs.Name = slicePointsName;
                foreach (var p in r.OuterPoints) doc.Objects.AddPoint(p, attrs);

                attrs.LayerIndex = projectedPointsTarget;
                attrs.Name = projectedPointsName;
                foreach (var p in r.InnerPoints) doc.Objects.AddPoint(p, attrs);

                if (r.InnerCurve != null && r.InnerCurve.IsValid)
                {
                    attrs.LayerIndex = projectedCurveTarget;
                    attrs.Name = projectedCurveName;
                    doc.Objects.AddCurve(r.InnerCurve, attrs);
                }

                // Inward-direction arrows: one LineCurve per sample point,
                // with EndArrowhead decoration. Always points from slice
                // point to projected point; the flip is already baked into
                // the point positions.
                var arrowAttrs = new ObjectAttributes
                {
                    LayerIndex = arrowLayer,
                    Name = "BracingInwardArrow",
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
                    attrs.Name = "BracingToolpath";
                    doc.Objects.AddCurve(r.Zigzag, attrs);
                }
            }

            // Both toolpath layers visible — they're the real output.
            // Point and arrow helpers hidden by default.
            if (outerToolpathLayer >= 0 && outerToolpathLayer < doc.Layers.Count)
                doc.Layers[outerToolpathLayer].IsVisible = true;
            if (innerToolpathLayer >= 0 && innerToolpathLayer < doc.Layers.Count)
                doc.Layers[innerToolpathLayer].IsVisible = true;
            if (outerPtsLayer >= 0 && outerPtsLayer < doc.Layers.Count)
                doc.Layers[outerPtsLayer].IsVisible = false;
            if (innerPtsLayer >= 0 && innerPtsLayer < doc.Layers.Count)
                doc.Layers[innerPtsLayer].IsVisible = false;
            if (arrowLayer >= 0 && arrowLayer < doc.Layers.Count)
                doc.Layers[arrowLayer].IsVisible = false;

            doc.Views.Redraw();
        }

        private void OnPreviewClayModelClick(object sender, EventArgs e)
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

                // Source layers: covers every mode that produces a toolpath
                // curve. Spiral mode emits one curve on Spiral Toolpath;
                // Layer / Layer+Bracing modes emit up to three. The clay
                // preview pipes whichever exist.
                var sourceLayerNames = new[]
                {
                    "3DP::Spiral Toolpath",
                    "3DP::Outer Toolpath",
                    "3DP::Inner Toolpath",
                    "3DP::Bracing Toolpath",
                };
                var srcIndices = new System.Collections.Generic.HashSet<int>();
                foreach (var name in sourceLayerNames)
                {
                    int idx = doc.Layers.FindByFullPath(name, -1);
                    if (idx >= 0) srcIndices.Add(idx);
                }
                if (srcIndices.Count == 0)
                {
                    SetStatus("No toolpath curves found — run Slice first");
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
                    SetStatus("No toolpath curves to preview — run Slice first");
                    return;
                }

                // Warn before starting — generation can take a while on dense
                // stacks and users were getting spooked by the hang.
                var confirm = MessageBox.Show(
                    this,
                    $"About to build the clay model preview from {curves.Count} " +
                    $"toolpath curves at {diameter:F2} mm bead diameter.\n\n" +
                    "This can take a few moments on dense geometry. Continue?",
                    "CCL_Clay3DP — Preview Clay Model",
                    MessageBoxButtons.YesNo,
                    MessageBoxType.Information,
                    MessageBoxDefaultButton.Yes);
                if (confirm != DialogResult.Yes)
                {
                    SetStatus("Preview cancelled");
                    return;
                }

                // Layer color and render material both follow the active clay
                // preset (Porcelain / Stoneware / Earthenware) so the preview
                // reads as actual material in Wireframe, Shaded, and Rendered
                // display modes alike.
                var clayPbr = ClayPreviewMaterials.Get(_settings.Clay.PresetName);
                int clayLayer = EnsureLayer(doc, "3DP::Clay Model", clayPbr.BaseColor);
                EnsureClayRenderMaterial(doc, clayLayer, _settings.Clay.PresetName, clayPbr);

                var attrs = new ObjectAttributes
                {
                    LayerIndex = clayLayer,
                    Name = "ClayModelMesh",
                };

                Rhino.UI.StatusBar.ShowProgressMeter(
                    0, curves.Count, "CCL_Clay3DP: building clay preview", true, true);
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

                    // Mesh.CreateFromCurvePipe can crash Rhino on smooth NURBS
                    // curves with many control points — the spiral toolpath
                    // (a cubic interpolation through thousands of sample points)
                    // is the prime offender. Discretize non-polyline input into
                    // a polyline at ~2 mm arc-length, capped at 50k vertices.
                    Curve pipeInput = c;
                    if (!c.TryGetPolyline(out _))
                    {
                        double len = c.GetLength();
                        int n = Math.Min(50000, Math.Max(32, (int)(len / 2.0)));
                        var ts = c.DivideByCount(n, true);
                        if (ts != null && ts.Length > 1)
                        {
                            var pts = new List<Point3d>(ts.Length);
                            foreach (var t in ts) pts.Add(c.PointAt(t));
                            pipeInput = new PolylineCurve(pts);
                        }
                    }

                    Mesh tube = null;
                    try
                    {
                        tube = Mesh.CreateFromCurvePipe(
                            pipeInput, radius, 12, 1,
                            MeshPipeCapStyle.Flat, false);
                    }
                    catch (Exception tubeEx)
                    {
                        RhinoApp.WriteLine(
                            $"[CCL_Clay3DP] Bead tube failed on curve: {tubeEx.Message}");
                    }

                    if (tube != null && tube.IsValid)
                    {
                        doc.Objects.AddMesh(tube, attrs);
                        pipedOk++;
                    }
                    else
                    {
                        failed++;
                    }

                    // Mesh spheres at polyline vertices — fills the miter gap
                    // at every direction change so the piped bead reads
                    // continuous. pipeInput is whatever we actually swept, so
                    // spheres line up with the mesh seams.
                    //
                    // For very long polylines (discretized spirals can hit
                    // 50k points) we stride so the total sphere count stays
                    // bounded. At stride N, adjacent spheres at 2 mm spacing
                    // sit 2N mm apart — still well under a bead diameter for
                    // reasonable strides, so the viz stays continuous.
                    if (pipeInput.TryGetPolyline(out Polyline pl) && pl.Count > 0)
                    {
                        int last = pl.IsClosed ? pl.Count - 1 : pl.Count;
                        const int maxSpheres = 2000;
                        int stride = Math.Max(1, (last + maxSpheres - 1) / maxSpheres);
                        for (int k = 0; k < last; k += stride)
                        {
                            var sphereMesh = Mesh.CreateFromSphere(
                                new Sphere(pl[k], radius), 8, 6);
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
                            $"CCL_Clay3DP: clay preview {i + 1}/{curves.Count}");
                        RhinoApp.Wait();
                    }
                }
                Rhino.UI.StatusBar.HideProgressMeter();

                doc.Views.Redraw();
                SetStatus(
                    $"Clay model preview: {pipedOk}/{curves.Count} meshes" +
                    (failed > 0 ? $" ({failed} failed)" : ""));
            }
            catch (Exception ex)
            {
                Rhino.UI.StatusBar.HideProgressMeter();
                SetStatus($"Preview error: {ex.Message}");
                RhinoApp.WriteLine($"[CCL_Clay3DP] Preview Clay Model failed: {ex}");
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

            int arrowLayer = EnsureLayer(doc, "3DP::Bracing Vectors",
                System.Drawing.Color.FromArgb(220, 40, 40));
            if (arrowLayer >= 0 && arrowLayer < doc.Layers.Count)
                doc.Layers[arrowLayer].IsVisible = true;

            var attrs = new ObjectAttributes
            {
                LayerIndex = arrowLayer,
                Name = "BracingInwardArrowPreview",
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
            OnSliceClick(sender, e);
            // Analyze + Send work for any slice mode now — _lastResult.Frames
            // is populated for Spiral and both Layer variants.
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
