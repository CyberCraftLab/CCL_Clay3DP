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

        // Stash the "translated X mm to origin" note so the completion
        // status can append it after RunSliceAndBake / RunLayerSlice
        // overwrite the panel's status with their own progress messages.
        private string _lastTranslationNote;

        // Set true when the post-slice toolpath exceeds the configured
        // build volume on any axis. OnSendToRoboDKClick checks this to
        // block sends that would crash the robot. Reset to false at the
        // top of every RunPipeline so each run is judged fresh.
        private bool _lastSliceOutOfBounds = false;

        // Post-auto-translate, PRE-shrinkage cached selection. Used by
        // the auto-rebuild paths so a settings or geometry change re-runs
        // the full pipeline (shrinkage scale + checks + slice) on the
        // same geometry without re-prompting the user to pick it.
        // Distinct from _lastGeometry, which the runners overwrite with
        // the post-shrinkage scaled selection (used by the heatmap
        // analyzer) — re-applying shrinkage to an already-scaled
        // selection would compound the scale.
        private GeometrySelection _lastRawGeometry;

        // Set true around our own doc.Objects.Transform call (the auto-
        // translate inside RunPipeline) so the ReplaceRhinoObject handler
        // ignores the event we triggered ourselves. Without this, the
        // auto-translate would re-fire the handler → rebuild → transform
        // → infinite loop. Rhino fires ReplaceRhinoObject synchronously
        // during the transform, so a simple bool flag is sufficient.
        private bool _suppressGeometryChangeRebuild = false;

        // Debounces the geometry-change auto-rebuild trigger so dragging
        // the Gumball (which fires ReplaceRhinoObject many times per
        // second) only kicks off ONE rebuild when the user releases.
        // 500 ms is long enough that a continuous drag coalesces, short
        // enough to feel responsive after a single click-and-drop edit.
        private UITimer _rebuildDebounce;


        // Workflow controls disabled until the user reviews Settings at least
        // once in this session. Avoids users clicking Slice with stale or
        // unconfigured params.
        private readonly List<Control> _gatedControls = new List<Control>();
        private bool _settingsReviewed = false;

        public CCL_Clay3DPPanel()
        {
            _settings = SettingsManager.Load();
            BuildUI();

            // Debounced auto-rebuild on source-geometry transform.
            // Subscribe to the global Rhino event; the handler filters
            // for our cached _lastRawGeometry.SourceObjectId. Both the
            // subscription and the timer are released in Dispose so a
            // panel close + re-open doesn't stack handlers or fire on
            // a disposed timer.
            _rebuildDebounce = new UITimer { Interval = 0.5 };
            _rebuildDebounce.Elapsed += OnRebuildDebounceElapsed;
            RhinoDoc.ReplaceRhinoObject += OnRhinoObjectReplaced;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RhinoDoc.ReplaceRhinoObject -= OnRhinoObjectReplaced;
                if (_rebuildDebounce != null)
                {
                    _rebuildDebounce.Elapsed -= OnRebuildDebounceElapsed;
                    _rebuildDebounce.Dispose();
                    _rebuildDebounce = null;
                }
            }
            base.Dispose(disposing);
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
            "3DP::Heatmap",
            "3DP::Outer Toolpath",
            "3DP::Bracing Outer Points",
            "3DP::Bracing Inner Points",
            "3DP::Inner Toolpath",
            "3DP::Bracing Toolpath",
            "3DP::Bracing Vectors",
            "3DP::Clay Model",
            "3DP::Print Position",
            "3DP::Skirt",
            "3DP::Base Contour",
            "3DP::Base Infill",
            "3DP::Build Volume",
        };

        private void OnSettingsClick(object sender, EventArgs e)
        {
            var dialog = new SettingsDialog(_settings);
            // ShowModal() with no parent centers on the screen rather than
            // on the docked panel.
            if (!dialog.ShowModal()) return;

            _settings = SettingsManager.Load();

            // First successful review unlocks the rest of the workflow.
            if (!_settingsReviewed)
            {
                _settingsReviewed = true;
                foreach (var c in _gatedControls) c.Enabled = true;
            }

            SetStatus("Settings saved");

            // After a settings change, any previously generated output is
            // stale (was computed with the old settings). Policy: wipe
            // and regenerate via the full pipeline — same code path as
            // the Slice button — so shrinkage compensation, build-volume
            // checks, etc., all re-apply with the new settings.
            //
            // Exception: Layer Slice + Outer Wall Bracing prompts the user
            // mid-run for flip direction + offset distance. Spamming those
            // prompts on every unrelated settings change is annoying, so
            // we leave that combo for the user to kick off with Slice.
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            // Always re-bake the build-volume wireframe so a Build Volume
            // settings change is visible even when there's nothing to
            // regenerate.
            BakeBuildVolume(doc, _settings.BuildVolume);

            if (!HasGeneratedContent(doc)) return;

            bool layerBracing = !_settings.Helix.SpiralSlice
                && _settings.Helix.OuterWallBracing;

            if (_lastRawGeometry != null && !layerBracing)
            {
                // Auto-rebuild via the full pipeline. The geometry is
                // already at origin from the previous slice, so the
                // pipeline's auto-translate is a no-op and the status
                // line reads "Geometry already at origin · scaled +X% …".
                SetStatus("Settings changed — regenerating...");
                RhinoApp.Wait();
                RunPipeline(_lastRawGeometry);
            }
            else
            {
                // Either the user has not done a slice yet (no cached
                // geometry) or we're in the Layer+Bracing combo. Clear
                // the stale output, re-bake the marker if we still have
                // a reference, and tell the user to click Slice.
                ClearGeneratedContent(doc);
                _lastResult = null;
                if (_lastRawGeometry != null)
                    BakePrintPositionMarker(doc, _lastRawGeometry);
                SetStatus(layerBracing
                    ? "Settings changed — click Slice to regenerate (Layer + Bracing needs interactive prompts)"
                    : "Generated layers cleared — click Slice to regenerate");
            }

            // RoboDK is never re-launched / re-sent automatically — that
            // is reserved for the explicit "Send to RoboDK" button (and
            // "Run All" while it exists). If the user previously sent a
            // job in this session, the RoboDK side is now stale; tell
            // them so they don't accidentally simulate / run the old
            // program.
            if (_hasSentToRoboDK)
                WarnRoboDKStaleAfterSettingsChange();
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

            // Everything else — gates, transforms, checks, slice, bake —
            // lives in RunPipeline so the auto-rebuild paths (settings
            // change, source-geometry transform) can re-run the same
            // logic against the cached _lastRawGeometry.
            RunPipeline(selection);
        }

        /// <summary>
        /// The full slice pipeline. Called from OnSliceClick (after a
        /// fresh user pick) and from OnSettingsClick's auto-rebuild
        /// branch (with the cached _lastRawGeometry).
        ///
        /// Steps, in order:
        ///   1. Outer Wall Bracing gate (free-form rejection — early so
        ///      we don't mutate the doc on a doomed run).
        ///   2. Reset _lastSliceOutOfBounds (every run is judged fresh).
        ///   3. Clear any previously generated output + cached result.
        ///   4. Auto-translate to world origin (PrusaSlicer convention).
        ///      Idempotent — already-at-origin geometry is a no-op.
        ///   5. Cache the post-translate selection in _lastRawGeometry
        ///      (pre-shrinkage — re-applying shrinkage to an already-
        ///      scaled selection would compound the scale).
        ///   6. Apply shrinkage compensation if enabled (uniform XYZ
        ///      about Point3d.Origin, which coincides with the part
        ///      footprint centroid on Z=0 after auto-translate).
        ///   7. Bake PrintPositionMarker + BuildVolume wireframes.
        ///   8. Pre-slice build-volume check + popup.
        ///   9. Run the slice (Spiral or Layer mode).
        ///  10. Post-slice build-volume check + popup + Send-block flag.
        /// </summary>
        private void RunPipeline(GeometrySelection selection)
        {
            // 0) Physical-feasibility gate. A layer height larger than
            // the bead diameter means the extruder is asked
            // to span a vertical gap larger than the bead's own
            // diameter — the bead would float in mid-air and not bond
            // to the previous layer. Reject early; user must adjust
            // either the layer height or the bead diameter in Settings.
            // Tiny epsilon so equal-to-diameter (layer == bead, perfect
            // squash-flush) is allowed.
            if (_settings.Helix.LayerHeight > _settings.Clay.BeadDiameter + 1e-6)
            {
                MessageBox.Show(
                    $"Layer height ({_settings.Helix.LayerHeight:F2} mm) exceeds " +
                    $"bead diameter ({_settings.Clay.BeadDiameter:F2} mm).\n\n" +
                    "This is physically impossible — clay can't span a vertical " +
                    "gap larger than its own diameter; the bead would not bond " +
                    "to the previous layer.\n\n" +
                    "Adjust either the layer height (lower) or the bead diameter " +
                    "(higher) in Settings, then click Slice again.",
                    "Layer height exceeds bead diameter",
                    MessageBoxButtons.OK, MessageBoxType.Warning);
                SetStatus("Layer height > bead diameter — slice cancelled");
                return;
            }

            // 1) Outer Wall Bracing gate — bracing's per-layer
            // DivideByCount + seam-align algorithm only behaves on ruled
            // / extrudable geometry. On free-form (sphere, organic Brep)
            // the seam wanders between layers and the bracing twists
            // into junk. Reject before mutating anything.
            if (!_settings.Helix.SpiralSlice
                && _settings.Helix.OuterWallBracing
                && !GeometryCurvature.IsRuled(selection))
            {
                MessageBox.Show(
                    "Outer Wall Bracing requires ruled / extruded geometry "
                    + "(cylinders, prisms, cones, planar extrusions). The "
                    + "selected part is free-form (has curvature in more "
                    + "than one parametric direction).\n\n"
                    + "Disable Outer Wall Bracing in Settings, or pick a "
                    + "ruled-surface part, then click Slice again.",
                    "Outer Wall Bracing not permitted",
                    MessageBoxButtons.OK, MessageBoxType.Warning);
                SetStatus("Bracing not permitted on free-form geometry — slice cancelled.");
                return;
            }

            // 2) Reset Send-block gate.
            _lastSliceOutOfBounds = false;

            // 3) Clean slate.
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
            {
                ClearGeneratedContent(doc);
                _lastResult = null;
            }

            // 4) Auto-translate to origin (idempotent for already-at-
            // origin geometry). Mirrors PrusaSlicer / Cura behavior;
            // the move is undoable with Ctrl+Z. The suppress flag keeps
            // our own transform from re-triggering the geometry-change
            // auto-rebuild handler (would otherwise infinite-loop).
            double translationDistance;
            var translation = ComputePrintingTranslation(
                selection, out translationDistance);
            if (doc != null && translationDistance >= 0.001
                && selection.SourceObjectId != Guid.Empty)
            {
                _suppressGeometryChangeRebuild = true;
                try
                {
                    doc.Objects.Transform(
                        selection.SourceObjectId, translation, true);
                }
                finally
                {
                    _suppressGeometryChangeRebuild = false;
                }
            }
            var printingSelection = ApplyTransform(selection, translation);

            // 5) Cache the post-translate, pre-shrinkage selection so
            // OnSettingsClick can auto-rebuild without re-prompting.
            _lastRawGeometry = printingSelection;

            // 6) Shrinkage compensation. The user's source Rhino object
            // stays at design size; only this in-memory copy grows.
            double shrinkageScaleApplied = 1.0;
            if (_settings.Clay.EnableShrinkageCompensation
                && _settings.Clay.ShrinkagePercent > 0.0
                && _settings.Clay.ShrinkagePercent < 100.0)
            {
                shrinkageScaleApplied =
                    1.0 / (1.0 - _settings.Clay.ShrinkagePercent / 100.0);
                var shrinkageScale = Transform.Scale(
                    Point3d.Origin, shrinkageScaleApplied);
                printingSelection = ApplyTransform(printingSelection, shrinkageScale);
            }

            // 7) Bake markers (build volume bake also surfaces any
            // build-volume settings change immediately).
            if (doc != null)
            {
                BakePrintPositionMarker(doc, printingSelection);
                BakeBuildVolume(doc, _settings.BuildVolume);
            }
            string translationMsg = translationDistance < 0.001
                ? "Geometry already at origin"
                : $"Geometry translated {translationDistance:F1} mm to " +
                  "origin for printing";
            if (shrinkageScaleApplied > 1.0)
            {
                double upscalePct = (shrinkageScaleApplied - 1.0) * 100.0;
                translationMsg +=
                    $" · scaled +{upscalePct:F1}% for " +
                    $"{_settings.Clay.ShrinkagePercent:F1}% shrinkage compensation";
            }
            _lastTranslationNote = translationMsg;
            SetStatus(translationMsg);

            // 8) Pre-slice build-volume check (fast fail on geometry
            // bbox; post-slice check uses the actual toolpath).
            var geomBbox = BuildVolumeCheck.SelectionBoundingBox(printingSelection);
            var preOverflow = BuildVolumeCheck.Check(geomBbox, _settings.BuildVolume);
            if (preOverflow.HasOverflow)
            {
                if (!ConfirmPreSliceBuildVolumeOverflow(preOverflow))
                {
                    SetStatus("Slice cancelled — geometry exceeds build volume");
                    return;
                }
            }

            // 9) Run slice.
            if (_settings.Helix.SpiralSlice)
                RunSliceAndBake(printingSelection);
            else
                RunLayerSlice(printingSelection);

            // 10) Post-slice build-volume check. Authoritative — uses
            // the full frame stream (skirt + base + part). On overflow,
            // popup + arm Send-block flag. Slice stays baked so user
            // can see exactly where it overflows.
            if (_lastResult != null && _lastResult.Frames.Count > 0)
            {
                var pathBbox = BuildVolumeCheck.FrameStreamBoundingBox(_lastResult);
                var postOverflow = BuildVolumeCheck.Check(pathBbox, _settings.BuildVolume);
                if (postOverflow.HasOverflow)
                {
                    _lastSliceOutOfBounds = true;
                    NotifyPostSliceBuildVolumeOverflow(postOverflow);
                    SetStatus("Toolpath exceeds build volume — Send to RoboDK blocked");
                }
            }
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

                // 1.5) Build base (Issue #10) before slicing the part — when
                // the base is enabled, the part body needs to sit on top of
                // it, so we shift the working geometry up by N · LayerHeight
                // and slice the shifted copy. When disabled, working* refers
                // to the original selection and the rest of the pipeline is
                // unchanged.
                var (workingBrep, workingMesh, baseResult, baseHeight) =
                    PrepareBaseGeometry(selection);

                // 2) Slice into contours
                List<Curve> contours;
                if (workingBrep != null)
                {
                    contours = ContourSlicer.SliceBrep(
                        workingBrep,
                        _settings.Helix.LayerHeight,
                        _settings.Height.HeightOffsetBottom,
                        _settings.Height.HeightOffsetTop,
                        reportProgress);
                }
                else
                {
                    contours = ContourSlicer.SliceMesh(
                        workingMesh,
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

                // 4) Compute frames — outward surface normal is the only
                // sensible default for clay printing (extruder approaches the
                // surface from outside). Hard-coded since the option was never
                // used and it complicated the settings UI.
                var frames = FrameComputer.ComputeFrames(
                    spiralPoints,
                    workingBrep,
                    workingMesh,
                    normalOutward: true,
                    reportProgress);

                // 5) Create spiral curve
                Rhino.UI.StatusBar.UpdateProgressMeter(85, true);
                Rhino.UI.StatusBar.SetMessagePane("CCL_Clay3DP: 85% — Creating spiral curve...");
                RhinoApp.Wait();
                var spiralCurve = SpiralInterpolator.CreateSpiralCurve(spiralPoints);

                // 6) Prepare to bake
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
                    Contours = contours,
                    LayerCount = contours.Count,
                };

                // 8) Bake to document
                BakeResults(_lastResult);

                // 9) Skirt and base. When the base is enabled, the skirt
                // sits at z=0 around the BASE footprint (built earlier
                // before the part was shifted up); the part's lowest
                // contour at z=N·h gets no skirt. When disabled, the
                // skirt is the part's own lowest contour offset, exactly
                // as before (Issue #8).
                if (baseResult != null)
                {
                    BakeSkirt(RhinoDoc.ActiveDoc, baseResult.SkirtCurve);
                    BakeBaseLayers(RhinoDoc.ActiveDoc, baseResult);
                    _lastResult.SkirtCurve = baseResult.SkirtCurve;
                    _lastResult.SkirtFrames = baseResult.SkirtFrames;
                    _lastResult.BaseFrames = baseResult.Frames;
                    _lastResult.BaseContourCurves = baseResult.ContourCurves;
                    _lastResult.InfillCurves = baseResult.InfillCurves;
                }
                else
                {
                    var skirt = SkirtBuilder.BuildSkirt(contours[0]);
                    BakeSkirt(RhinoDoc.ActiveDoc, skirt);
                    _lastResult.SkirtCurve = skirt;
                    _lastResult.SkirtFrames = SkirtBuilder.SampleSkirtFrames(
                        skirt, _settings.Helix.FramesPerLayer);
                }

                Rhino.UI.StatusBar.UpdateProgressMeter(100, true);
                Rhino.UI.StatusBar.HideProgressMeter();
                SetStatus(WithTranslationNote("Spiral slice complete"));
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
            // panel. The SpiralCurve is the actual toolpath output. The
            // ribbon mesh viz was retired too — frames are still computed
            // and sent to RoboDK, just not baked as a visual aid.
            int spiralLayer = EnsureLayer(doc, "3DP::Spiral Toolpath",
                System.Drawing.Color.FromArgb(255, 0, 0));

            var attrs = new ObjectAttributes();

            // Bake spiral curve
            if (result.SpiralCurve != null && result.SpiralCurve.IsValid)
            {
                attrs.LayerIndex = spiralLayer;
                attrs.Name = "SpiralToolpath";
                doc.Objects.AddCurve(result.SpiralCurve, attrs);
            }

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

        // Translation that lands the input bbox bottom-center at world
        // origin (XY centroid → 0,0; lowest Z → 0). Identity if bbox is
        // invalid or empty. `distance` is the magnitude of the translation,
        // used by the caller for status messages.
        private static Transform ComputePrintingTranslation(
            GeometrySelection sel, out double distance)
        {
            distance = 0.0;
            if (sel == null) return Transform.Identity;
            BoundingBox bbox = BoundingBox.Empty;
            if (sel.Brep != null) bbox = sel.Brep.GetBoundingBox(true);
            else if (sel.Mesh != null) bbox = sel.Mesh.GetBoundingBox(true);
            if (!bbox.IsValid) return Transform.Identity;

            var center = bbox.Center;
            double dx = -center.X;
            double dy = -center.Y;
            double dz = -bbox.Min.Z;
            distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return Transform.Translation(new Vector3d(dx, dy, dz));
        }

        // Returns a translated copy of the selection. Originals are not
        // modified — a Brep is duplicated and a Mesh is duplicated before
        // Transform is applied. Identity transforms are returned as-is.
        private static GeometrySelection ApplyTransform(
            GeometrySelection sel, Transform xform)
        {
            if (sel == null) return null;
            if (xform == Transform.Identity) return sel;
            // Propagate the SourceObjectId to the transformed copy so the
            // panel can still hide / restore the user's original geometry
            // even though the slice pipeline operates on the translated
            // duplicate.
            var result = new GeometrySelection
            {
                SourceObjectId = sel.SourceObjectId,
            };
            if (sel.Brep != null)
            {
                var b = sel.Brep.DuplicateBrep();
                b.Transform(xform);
                result.Brep = b;
            }
            if (sel.Mesh != null)
            {
                var m = sel.Mesh.DuplicateMesh();
                m.Transform(xform);
                result.Mesh = m;
            }
            return result;
        }

        // Bake a small RGB axis cross at the world origin so the user
        // can see where the print will physically land. Single dedicated
        // layer "3DP::Print Position", visible by default. The earlier
        // bounding-box wireframe was removed (too much visual clutter for
        // not enough information).
        private static void BakePrintPositionMarker(
            RhinoDoc doc, GeometrySelection sel)
        {
            if (doc == null || sel == null) return;
            BoundingBox bbox = BoundingBox.Empty;
            if (sel.Brep != null) bbox = sel.Brep.GetBoundingBox(true);
            else if (sel.Mesh != null) bbox = sel.Mesh.GetBoundingBox(true);
            if (!bbox.IsValid) return;

            int layerIdx = EnsureLayer(
                doc, "3DP::Print Position", System.Drawing.Color.DimGray);
            if (layerIdx < 0) return;
            if (layerIdx < doc.Layers.Count)
                doc.Layers[layerIdx].IsVisible = true;

            // RGB axis cross. Length scales with the part bbox so it
            // stays visible on both small and large prints, with a 20mm
            // floor.
            double diag = bbox.Diagonal.Length;
            double axisLen = Math.Max(20.0, diag * 0.1);
            var origin = Point3d.Origin;
            var axes = new[]
            {
                new { Dir = new Vector3d(axisLen, 0, 0),
                      Name = "PrintPositionAxisX",
                      Color = System.Drawing.Color.Red },
                new { Dir = new Vector3d(0, axisLen, 0),
                      Name = "PrintPositionAxisY",
                      Color = System.Drawing.Color.Green },
                new { Dir = new Vector3d(0, 0, axisLen),
                      Name = "PrintPositionAxisZ",
                      Color = System.Drawing.Color.Blue },
            };
            foreach (var ax in axes)
            {
                var attrs = new ObjectAttributes
                {
                    LayerIndex = layerIdx,
                    Name = ax.Name,
                    ObjectDecoration = ObjectDecoration.EndArrowhead,
                    ColorSource = ObjectColorSource.ColorFromObject,
                    ObjectColor = ax.Color,
                };
                var line = new LineCurve(origin, origin + ax.Dir);
                if (line.IsValid) doc.Objects.AddCurve(line, attrs);
            }

            doc.Views.Redraw();
        }

        // Bake a wireframe box showing the cell's printable space:
        // 12 edges from (XMin, YMin, 0) to (XMax, YMax, Height). The
        // box always sits on the build plate (Z starts at 0). Bounds
        // come from PipelineSettings.BuildVolume so the user can adjust
        // them in Settings if they have a different cell.
        private static void BakeBuildVolume(
            RhinoDoc doc, BuildVolumeSettings bv)
        {
            if (doc == null || bv == null) return;
            // Validate ranges — silently skip on degenerate input rather
            // than throw mid-slice.
            if (bv.XMin >= bv.XMax) return;
            if (bv.YMin >= bv.YMax) return;
            if (bv.Height <= 0) return;

            int layerIdx = EnsureLayer(
                doc, "3DP::Build Volume", System.Drawing.Color.DarkCyan);
            if (layerIdx < 0) return;
            if (layerIdx < doc.Layers.Count)
                doc.Layers[layerIdx].IsVisible = true;

            var c = new Point3d[8]
            {
                new Point3d(bv.XMin, bv.YMin, 0.0),       // 0
                new Point3d(bv.XMax, bv.YMin, 0.0),       // 1
                new Point3d(bv.XMax, bv.YMax, 0.0),       // 2
                new Point3d(bv.XMin, bv.YMax, 0.0),       // 3
                new Point3d(bv.XMin, bv.YMin, bv.Height), // 4
                new Point3d(bv.XMax, bv.YMin, bv.Height), // 5
                new Point3d(bv.XMax, bv.YMax, bv.Height), // 6
                new Point3d(bv.XMin, bv.YMax, bv.Height), // 7
            };
            int[,] edges = new int[,]
            {
                { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 }, // bottom rect
                { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 }, // top rect
                { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }, // verticals
            };
            var attrs = new ObjectAttributes
            {
                LayerIndex = layerIdx,
                Name = "BuildVolumeEdge",
            };
            for (int i = 0; i < edges.GetLength(0); i++)
            {
                var line = new LineCurve(c[edges[i, 0]], c[edges[i, 1]]);
                if (line.IsValid) doc.Objects.AddCurve(line, attrs);
            }
            doc.Views.Redraw();
        }

        // Bake the skirt curve on its own layer so the user can verify
        // the offset before sending to the robot. The skirt is always
        // produced from the lowest contour (15 mm outward, fixed) and
        // is the first path the robot follows.
        private static void BakeSkirt(RhinoDoc doc, Curve skirt)
        {
            if (doc == null || skirt == null) return;
            int layerIdx = EnsureLayer(
                doc, "3DP::Skirt", System.Drawing.Color.Blue);
            if (layerIdx < 0) return;
            if (layerIdx < doc.Layers.Count)
                doc.Layers[layerIdx].IsVisible = true;

            var attrs = new ObjectAttributes
            {
                LayerIndex = layerIdx,
                Name = "Skirt",
            };
            doc.Objects.AddCurve(skirt, attrs);
            doc.Views.Redraw();
        }

        /// <summary>
        /// Bake the per-layer base contours and infill polylines onto
        /// dedicated sublayers so the user can preview the base before
        /// sending to RoboDK. One contour curve and (usually) one infill
        /// polyline per base layer; nulls in InfillCurves are skipped
        /// silently — they mean infill failed for that layer (e.g.,
        /// scan returned no segments) but the contour still printed.
        /// </summary>
        private static void BakeBaseLayers(RhinoDoc doc, BaseResult baseResult)
        {
            if (doc == null || baseResult == null) return;

            int contourLayer = EnsureLayer(
                doc, "3DP::Base Contour", System.Drawing.Color.SteelBlue);
            int infillLayer = EnsureLayer(
                doc, "3DP::Base Infill", System.Drawing.Color.Teal);

            if (contourLayer >= 0 && contourLayer < doc.Layers.Count)
                doc.Layers[contourLayer].IsVisible = true;
            if (infillLayer >= 0 && infillLayer < doc.Layers.Count)
                doc.Layers[infillLayer].IsVisible = true;

            for (int i = 0; i < baseResult.ContourCurves.Count; i++)
            {
                var c = baseResult.ContourCurves[i];
                if (c == null || contourLayer < 0) continue;
                doc.Objects.AddCurve(c, new ObjectAttributes
                {
                    LayerIndex = contourLayer,
                    Name = $"Base Contour L{i}",
                });
            }
            for (int i = 0; i < baseResult.InfillCurves.Count; i++)
            {
                var c = baseResult.InfillCurves[i];
                if (c == null || infillLayer < 0) continue;
                doc.Objects.AddCurve(c, new ObjectAttributes
                {
                    LayerIndex = infillLayer,
                    Name = $"Base Infill L{i}",
                });
            }
            doc.Views.Redraw();
        }

        /// <summary>
        /// If base printing is enabled and a usable lowest contour can be
        /// extracted from the part, build the base toolpath and return
        /// translated working copies of the part geometry shifted up by
        /// N · LayerHeight so existing slice / frame code can run against
        /// them unchanged. When base is disabled (or the contour is
        /// unrecoverable) returns the input geometry references and a
        /// null base — caller treats that as the no-base path.
        /// </summary>
        private (Brep workingBrep, Mesh workingMesh, BaseResult baseResult, double baseHeight)
            PrepareBaseGeometry(GeometrySelection selection)
        {
            if (selection == null || !_settings.Base.EnableBase)
                return (selection?.Brep, selection?.Mesh, null, 0.0);

            // Sample the part's actual lowest cross-section, before any
            // shift. ContourSlicer's convention is bbox.Min.Z + 0.01 to
            // skip the degenerate ground-plane tangent slice.
            BoundingBox bbox = BoundingBox.Empty;
            if (selection.Brep != null) bbox = selection.Brep.GetBoundingBox(true);
            else if (selection.Mesh != null) bbox = selection.Mesh.GetBoundingBox(true);
            if (!bbox.IsValid) return (selection.Brep, selection.Mesh, null, 0.0);

            double zSlice = bbox.Min.Z + 0.01;
            Curve lowestContour = selection.Brep != null
                ? ContourSlicer.SliceBrepAt(selection.Brep, zSlice)
                : ContourSlicer.SliceMeshAt(selection.Mesh, zSlice);

            if (lowestContour == null || !lowestContour.IsClosed)
            {
                RhinoApp.WriteLine(
                    "[CCL_Clay3DP] Base enabled but the part's lowest contour " +
                    "is not closed/recoverable — skipping base for this slice.");
                return (selection.Brep, selection.Mesh, null, 0.0);
            }

            var baseResult = BaseBuilder.Build(
                lowestContour,
                _settings.Base,
                _settings.Helix.LayerHeight,
                _settings.Clay.BeadDiameter,
                _settings.Helix.FramesPerLayer);

            if (baseResult.LayerCount == 0)
                return (selection.Brep, selection.Mesh, null, 0.0);

            double baseHeight = baseResult.TopZ;

            // Translate working copies — never the originals — so other
            // panel state (cached selection, re-slice, restore-on-cancel)
            // sees the user's geometry untouched.
            Brep workingBrep = null;
            Mesh workingMesh = null;
            if (selection.Brep != null)
            {
                workingBrep = selection.Brep.DuplicateBrep();
                workingBrep.Translate(0.0, 0.0, baseHeight);
            }
            if (selection.Mesh != null)
            {
                workingMesh = selection.Mesh.DuplicateMesh();
                workingMesh.Translate(0.0, 0.0, baseHeight);
            }

            return (workingBrep, workingMesh, baseResult, baseHeight);
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
            // Hard block: the most recent slice's toolpath exceeds the
            // configured build volume. Sending would crash the robot.
            // User must re-slice with the issue resolved (move part,
            // shrink build volume, lower shrinkage %, etc.).
            if (_lastSliceOutOfBounds)
            {
                MessageBox.Show(
                    "The last slice's toolpath extends past the build " +
                    "volume — sending it to RoboDK would crash the robot.\n\n" +
                    "Adjust the part position, build volume, or shrinkage % " +
                    "in Settings, then run Slice again.",
                    "Send blocked — toolpath out of bounds",
                    MessageBoxButtons.OK, MessageBoxType.Warning);
                SetStatus("Send to RoboDK blocked — toolpath out of bounds");
                return;
            }

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

                // Follow-curve-normal mode only applies to Spiral toolpaths;
                // Layer-mode frames are world-Z by construction anyway, but
                // we gate explicitly on SpiralSlice so the flag's intent is
                // unambiguous at the call site.
                bool followNormal = _settings.Helix.SpiralSlice
                    && _settings.Helix.SpiralFollowsCurveNormal;

                // Print order is skirt → base (if any) → part. Concatenate
                // into one continuous frame stream so RoboDK traces it as a
                // single curve: with ApproachRetractAll=0 in the template
                // that gives one approach at the skirt's first point, then
                // op-speed linear moves all the way to the part's last
                // point (no Z lifts, no extruder toggles between blocks),
                // then one retract. SkirtFrames and BaseFrames both carry
                // YAxis=+Z so the build plate stays flat under them even
                // when followCurveNormal is true for the part body.
                var combinedFrames = new List<Rhino.Geometry.Plane>(
                    (_lastResult.SkirtFrames?.Count ?? 0)
                    + (_lastResult.BaseFrames?.Count ?? 0)
                    + _lastResult.Frames.Count);
                if (_lastResult.SkirtFrames != null)
                    combinedFrames.AddRange(_lastResult.SkirtFrames);
                if (_lastResult.BaseFrames != null)
                    combinedFrames.AddRange(_lastResult.BaseFrames);
                combinedFrames.AddRange(_lastResult.Frames);

                string jsonPath = RoboDK.FrameSerializer.SerializeToFile(
                    combinedFrames, _settings.Robot,
                    _settings.Helix.LayerHeight, followNormal);

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

        /// <summary>
        /// One-shot informational warning: settings changed and the Rhino
        /// toolpath was cleared, but RoboDK still holds the toolpath from
        /// the previous Send. The user must Slice + Send again to refresh
        /// it. Fires only when auto-rebuild can't propagate (Layer mode, or
        /// when the geometry reference was lost).
        /// </summary>
        private void WarnRoboDKStaleAfterSettingsChange()
        {
            MessageBox.Show(
                "Settings changed and the previous Rhino toolpath was cleared, " +
                "but the RoboDK session still holds the toolpath from the " +
                "previous Send.\n\n" +
                "Click Slice and then Send to RoboDK to refresh it before " +
                "running the next print.",
                "CCL_Clay3DP — RoboDK out of sync",
                MessageBoxButtons.OK,
                MessageBoxType.Warning);
        }

        private void RunLayerSlice(GeometrySelection selection)
        {
            try
            {
                double layerHeight = _settings.Helix.LayerHeight;
                bool bracing = _settings.Helix.OuterWallBracing;

                // Issue #10: build base before slicing so the part body
                // sits on top of it (shifted up by N · LayerHeight).
                var (workingBrep, workingMesh, baseResult, baseHeight) =
                    PrepareBaseGeometry(selection);

                SetStatus($"Layer slice: slicing at {layerHeight} mm...");
                RhinoApp.Wait();
                List<Curve> contours = workingBrep != null
                    ? ContourSlicer.SliceBrep(
                        workingBrep, layerHeight,
                        _settings.Height.HeightOffsetBottom,
                        _settings.Height.HeightOffsetTop)
                    : ContourSlicer.SliceMesh(
                        workingMesh, layerHeight,
                        _settings.Height.HeightOffsetBottom,
                        _settings.Height.HeightOffsetTop);

                if (!bracing)
                {
                    // Simple layer slice — just the outer contours.
                    BakeLayerContours(contours);

                    // Skirt: when base is enabled, source from the base
                    // footprint at z=0; otherwise from the part's lowest
                    // contour (Issue #8).
                    Curve skirtNoBrace;
                    List<Plane> skirtNoBraceFrames;
                    if (baseResult != null)
                    {
                        skirtNoBrace = baseResult.SkirtCurve;
                        skirtNoBraceFrames = baseResult.SkirtFrames;
                        BakeBaseLayers(RhinoDoc.ActiveDoc, baseResult);
                    }
                    else
                    {
                        skirtNoBrace = SkirtBuilder.BuildSkirt(contours[0]);
                        skirtNoBraceFrames = SkirtBuilder.SampleSkirtFrames(
                            skirtNoBrace, _settings.Helix.FramesPerLayer);
                    }
                    BakeSkirt(RhinoDoc.ActiveDoc, skirtNoBrace);

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
                        SkirtCurve = skirtNoBrace,
                        SkirtFrames = skirtNoBraceFrames,
                        BaseFrames = baseResult?.Frames ?? new List<Plane>(),
                        BaseContourCurves = baseResult?.ContourCurves ?? new List<Curve>(),
                        InfillCurves = baseResult?.InfillCurves ?? new List<Curve>(),
                    };

                    SetStatus(WithTranslationNote(
                        $"Layer slice complete: {contours.Count} contours"));
                    return;
                }

                // Outer Wall Bracing: preview inward arrows so the user can
                // flip the side before picking the offset distance. Contact-
                // point count is decoupled from FramesPerLayer (Issue #11)
                // so bracing density can be tuned independently of toolpath
                // sampling. BracingContactPoints means "number of times the
                // bracing touches the wall" — we double it for the generator
                // so each pair (outer-touch, inner-anchor) accounts for one
                // wall contact, matching what the user counts by eye.
                int numPoints = 2 * _settings.Helix.BracingContactPoints;
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
                //   Outer Toolpath → Bracing Toolpath
                // The Outer Wall is always the slice contour, regardless
                // of flip direction. The projected curve (inward or outward
                // depending on flip) is just the bracing's anchor — neither
                // baked nor printed.
                var layerPts = new List<Point3d>();
                for (int i = 0; i < goodContours.Count; i++)
                {
                    if (goodContours[i] != null)
                        AddCurvePoints(goodContours[i], layerPts);
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

                // Skirt: with base enabled, the skirt is the base's
                // outward offset at z=0; otherwise the absolute lowest
                // contour's offset (NOT goodContours[0] — the skirt is
                // independent of which layers passed the bracing filter
                // per Issue #8). Base layers also get baked for preview.
                if (baseResult != null)
                {
                    BakeSkirt(RhinoDoc.ActiveDoc, baseResult.SkirtCurve);
                    BakeBaseLayers(RhinoDoc.ActiveDoc, baseResult);
                    _lastResult.SkirtCurve = baseResult.SkirtCurve;
                    _lastResult.SkirtFrames = baseResult.SkirtFrames;
                    _lastResult.BaseFrames = baseResult.Frames;
                    _lastResult.BaseContourCurves = baseResult.ContourCurves;
                    _lastResult.InfillCurves = baseResult.InfillCurves;
                }
                else
                {
                    var skirtBraced = SkirtBuilder.BuildSkirt(contours[0]);
                    BakeSkirt(RhinoDoc.ActiveDoc, skirtBraced);
                    _lastResult.SkirtCurve = skirtBraced;
                    _lastResult.SkirtFrames = SkirtBuilder.SampleSkirtFrames(
                        skirtBraced, _settings.Helix.FramesPerLayer);
                }

                SetStatus(WithTranslationNote(
                    $"Layer slice + bracing complete: {results.Count} layers" +
                    (skipped > 0 ? $", {skipped} skipped" : "")));
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
            int outerPtsLayer = EnsureLayer(doc, "3DP::Bracing Outer Points",
                System.Drawing.Color.FromArgb(40, 40, 40));
            int innerPtsLayer = EnsureLayer(doc, "3DP::Bracing Inner Points",
                System.Drawing.Color.FromArgb(40, 80, 200));
            int zzLayer = EnsureLayer(doc, "3DP::Bracing Toolpath",
                System.Drawing.Color.FromArgb(0, 200, 255));
            int arrowLayer = EnsureLayer(doc, "3DP::Bracing Vectors",
                System.Drawing.Color.FromArgb(220, 40, 40));

            // We only bake the outer toolpath (and bracing); the projected
            // inner curve is computed for bracing geometry but not printed
            // and not baked. Flip swaps which underlying curve is the
            // geometrically outer one, and which set of points is which.
            int slicePointsTarget = flipInward ? innerPtsLayer : outerPtsLayer;
            int projectedPointsTarget = flipInward ? outerPtsLayer : innerPtsLayer;
            string slicePointsName = flipInward ? "BracingInnerPoint" : "BracingOuterPoint";
            string projectedPointsName = flipInward ? "BracingOuterPoint" : "BracingInnerPoint";

            var attrs = new ObjectAttributes();

            for (int i = 0; i < contours.Count; i++)
            {
                var contour = contours[i];
                var r = results[i];

                // The Outer Wall is ALWAYS the slice contour (= the input
                // Brep/Mesh's actual outline at this Z). Flip only changes
                // which side the bracing extends to (inward by default,
                // outward when flipped) — it doesn't change which curve
                // is the outer wall.
                if (contour != null && contour.IsValid)
                {
                    attrs.LayerIndex = outerToolpathLayer;
                    attrs.Name = "OuterToolpath";
                    doc.Objects.AddCurve(contour, attrs);
                }

                attrs.LayerIndex = slicePointsTarget;
                attrs.Name = slicePointsName;
                foreach (var p in r.OuterPoints) doc.Objects.AddPoint(p, attrs);

                attrs.LayerIndex = projectedPointsTarget;
                attrs.Name = projectedPointsName;
                foreach (var p in r.InnerPoints) doc.Objects.AddPoint(p, attrs);

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

            // Outer toolpath visible — real print path. Point and arrow
            // helpers hidden by default.
            if (outerToolpathLayer >= 0 && outerToolpathLayer < doc.Layers.Count)
                doc.Layers[outerToolpathLayer].IsVisible = true;
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
                double layerHeight = _settings.Helix.LayerHeight;
                if (layerHeight <= 0)
                {
                    SetStatus("Layer height must be > 0 in Toolpath settings");
                    return;
                }
                // Same physical-feasibility gate as RunPipeline — preview
                // can be invoked on stale toolpath layers without re-
                // slicing, so we re-check here too.
                if (layerHeight > diameter + 1e-6)
                {
                    MessageBox.Show(
                        $"Layer height ({layerHeight:F2} mm) exceeds bead diameter " +
                        $"({diameter:F2} mm).\n\n" +
                        "Preview can't represent a bead that doesn't bond. Adjust " +
                        "either parameter in Settings, then re-slice and preview.",
                        "Layer height exceeds bead diameter",
                        MessageBoxButtons.OK, MessageBoxType.Warning);
                    SetStatus("Preview cancelled — layer height > bead diameter");
                    return;
                }

                // Cross-section dimensions. Layer height squashes the bead
                // vertically; mass / cross-section-area conservation means
                // it spreads horizontally:
                //   π × (D/2)²  =  π × (W/2) × (H/2)
                //   →  W  =  D² / H
                // When H == D the bead stays circular (W = D); when H < D
                // it widens into an ellipse with minor axis vertical.
                double widthRadius, heightRadius;
                if (Math.Abs(layerHeight - diameter) < 1e-6)
                {
                    // Circle — preserved exactly so the existing optimised
                    // CreateFromCurvePipe path is used.
                    widthRadius  = diameter * 0.5;
                    heightRadius = diameter * 0.5;
                }
                else
                {
                    widthRadius  = (diameter * diameter) / (2.0 * layerHeight);
                    heightRadius = layerHeight * 0.5;
                }
                bool elliptical = Math.Abs(widthRadius - heightRadius) > 1e-6;

                // Source layers: covers every mode that produces a toolpath
                // curve. Spiral mode emits one curve on Spiral Toolpath;
                // Layer / Layer+Bracing modes emit up to three. Skirt and
                // Base layers are included so the preview shows the full
                // print stack — what the robot actually deposits — not
                // just the part body.
                var sourceLayerNames = new[]
                {
                    "3DP::Spiral Toolpath",
                    "3DP::Outer Toolpath",
                    "3DP::Inner Toolpath",
                    "3DP::Bracing Toolpath",
                    "3DP::Skirt",
                    "3DP::Base Contour",
                    "3DP::Base Infill",
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
                string crossSectionMsg = elliptical
                    ? $"elliptical bead {widthRadius * 2.0:F2} × {heightRadius * 2.0:F2} mm " +
                      $"(area-conserved from {diameter:F2} mm round @ {layerHeight:F2} mm layer)"
                    : $"{diameter:F2} mm round bead";
                var confirm = MessageBox.Show(
                    $"About to build the clay model preview from {curves.Count} " +
                    $"toolpath curves at {crossSectionMsg}.\n\n" +
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
                        if (elliptical)
                        {
                            // Manual elliptical sweep so the cross-section
                            // can have a different vertical (height) and
                            // horizontal (width) extent — Rhino's
                            // CreateFromCurvePipe is circular-only.
                            tube = BuildEllipticalTube(
                                pipeInput, widthRadius, heightRadius, 12);
                        }
                        else
                        {
                            tube = Mesh.CreateFromCurvePipe(
                                pipeInput, widthRadius, 12, 1,
                                MeshPipeCapStyle.Flat, false);
                        }
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
                            var fillerMesh = MakeBeadFiller(pl[k], widthRadius, heightRadius);
                            if (fillerMesh != null && fillerMesh.IsValid)
                                doc.Objects.AddMesh(fillerMesh, attrs);
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

        /// <summary>
        /// Build a tube mesh of elliptical cross-section along a polyline.
        /// The ellipse's vertical (Z) semi-axis is fixed in world Z; the
        /// horizontal semi-axis lies in the XY plane,
        /// perpendicular to the local curve tangent. This matches the
        /// physical reality of a clay bead squashed by the build plate
        /// or previous layer:
        ///   * heightRadius = layer height / 2  (vertical squash extent)
        ///   * widthRadius  = D² / (2H) / 2  (lateral spread, area-
        ///     conserved with the original D-diameter circular bead)
        ///
        /// segments = number of vertices around each ellipse ring.
        /// 12 matches what Mesh.CreateFromCurvePipe uses by default.
        ///
        /// Closed input polylines (skirt, contour loops) connect last
        /// ring → first ring with no caps. Open polylines (spirals,
        /// infill zigzags) get fan-triangulated end caps so the tube
        /// reads as solid.
        /// </summary>
        /// <summary>
        /// Build a single bead-shaped filler mesh at <paramref name="centre"/>
        /// matching the cross-section of the surrounding tube. When the
        /// cross-section is circular (widthRadius == heightRadius) this is
        /// just a sphere; otherwise it's a unit sphere scaled non-uniformly
        /// to (W, W, H). Used to fill miter gaps at polyline vertices in
        /// the Preview Clay Model bake.
        /// </summary>
        private static Mesh MakeBeadFiller(
            Point3d centre, double widthRadius, double heightRadius)
        {
            if (Math.Abs(widthRadius - heightRadius) < 1e-6)
                return Mesh.CreateFromSphere(new Sphere(centre, widthRadius), 8, 6);

            var m = Mesh.CreateFromSphere(new Sphere(Point3d.Origin, 1.0), 8, 6);
            if (m == null) return null;
            m.Transform(Transform.Scale(
                Plane.WorldXY, widthRadius, widthRadius, heightRadius));
            m.Translate(centre - Point3d.Origin);
            return m;
        }

        private static Mesh BuildEllipticalTube(
            Curve curve,
            double widthRadius,
            double heightRadius,
            int segments)
        {
            if (curve == null) return null;
            if (!curve.TryGetPolyline(out Polyline pl) || pl.Count < 2) return null;
            if (segments < 3) segments = 3;

            int n = pl.Count;
            bool closed = pl.IsClosed;
            // Closed polylines from Rhino include a duplicated last
            // vertex equal to the first. Drop it so the ring math
            // doesn't generate a degenerate sliver.
            if (closed && n > 2 && pl[0].DistanceTo(pl[n - 1]) < 1e-9)
                n -= 1;

            var mesh = new Mesh();

            // Build one ring of `segments` vertices around each polyline
            // vertex. Tangent uses central differences for interior
            // vertices and end-of-segment for endpoints / closed-loop
            // wraparound.
            for (int i = 0; i < n; i++)
            {
                Point3d p = pl[i];

                Vector3d tangent;
                if (closed)
                {
                    int prev = (i - 1 + n) % n;
                    int next = (i + 1) % n;
                    tangent = pl[next] - pl[prev];
                }
                else if (i == 0)
                {
                    tangent = pl[1] - pl[0];
                }
                else if (i == n - 1)
                {
                    tangent = pl[i] - pl[i - 1];
                }
                else
                {
                    tangent = pl[i + 1] - pl[i - 1];
                }
                if (!tangent.Unitize())
                    tangent = Vector3d.XAxis;

                // Cross-section frame: horizontal-perp axis lies in
                // world XY (tangent × Z); vertical axis is world Z.
                Vector3d zUp = Vector3d.ZAxis;
                Vector3d horiz = Vector3d.CrossProduct(zUp, tangent);
                if (!horiz.Unitize())
                {
                    // Tangent is purely vertical (rare — spiral going
                    // straight up at a singular point). Fall back to
                    // world X so the ring is at least non-degenerate.
                    horiz = Vector3d.XAxis;
                }

                for (int s = 0; s < segments; s++)
                {
                    double angle = 2.0 * Math.PI * s / segments;
                    Vector3d offset =
                        widthRadius  * Math.Cos(angle) * horiz +
                        heightRadius * Math.Sin(angle) * zUp;
                    mesh.Vertices.Add(p + offset);
                }
            }

            // Connect adjacent rings with quad faces. Closed: every
            // ring has a successor (last → first). Open: skip the last.
            int ringCount = closed ? n : n - 1;
            for (int i = 0; i < ringCount; i++)
            {
                int next = (i + 1) % n;
                int baseI = i * segments;
                int baseN = next * segments;
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    mesh.Faces.AddFace(
                        baseI + s,
                        baseI + s1,
                        baseN + s1,
                        baseN + s);
                }
            }

            // End caps for open polylines — fan triangles from a centre
            // vertex at each endpoint to that ring's perimeter.
            if (!closed)
            {
                int firstCentre = mesh.Vertices.Add(pl[0]);
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    mesh.Faces.AddFace(firstCentre, s1, s);
                }

                int lastRingStart = (n - 1) * segments;
                int lastCentre = mesh.Vertices.Add(pl[n - 1]);
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    mesh.Faces.AddFace(
                        lastCentre,
                        lastRingStart + s,
                        lastRingStart + s1);
                }
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        private void OnRunAllClick(object sender, EventArgs e)
        {
            OnSliceClick(sender, e);
            // Analyze + Send work for any slice mode now — _lastResult.Frames
            // is populated for Spiral and both Layer variants.
            if (_lastResult == null || _lastResult.Frames.Count == 0) return;

            // Short-circuit when the slice is out of bounds: Send would
            // be blocked anyway, and Analyze on a path the user can't
            // print just wastes their time. The post-slice popup has
            // already informed them; the panel status reflects it.
            if (_lastSliceOutOfBounds) return;

            OnAnalyzeClick(sender, e);
            OnSendToRoboDKClick(sender, e);
        }

        // ---------------------------------------------------------------
        // Build-volume overflow popups.
        //
        // Pre-slice variant: actionable — user can Cancel (abort the slice
        // before any cycles are spent) or Continue Anyway (proceed to slice
        // and let the post-slice check have the final word).
        //
        // Post-slice variant: informational — the slice and bake have
        // already happened. The user can inspect the toolpath in Rhino
        // to see exactly where it overflows, but Send-to-RoboDK is hard-
        // blocked by _lastSliceOutOfBounds until they re-slice with the
        // issue resolved. So a single OK button is the only useful action.
        // ---------------------------------------------------------------

        private bool ConfirmPreSliceBuildVolumeOverflow(BuildVolumeCheck.Overflow ov)
        {
            return ShowBuildVolumeOverflowDialog(
                bodyIntro:
                    "The selected geometry (after auto-translate and " +
                    "shrinkage compensation, if enabled) extends past the " +
                    "configured build volume:",
                overflow: ov,
                bodyOutro:
                    "Slicing will produce a toolpath that won't fit. Send " +
                    "to RoboDK will be blocked even if you continue.\n\n" +
                    "Adjust the build volume, the part position, or the " +
                    "shrinkage % to fix.",
                offerContinue: true);
        }

        private void NotifyPostSliceBuildVolumeOverflow(BuildVolumeCheck.Overflow ov)
        {
            ShowBuildVolumeOverflowDialog(
                bodyIntro:
                    "The completed toolpath (skirt + base + part) extends " +
                    "past the configured build volume:",
                overflow: ov,
                bodyOutro:
                    "The slice has been baked so you can inspect where it " +
                    "overflows in Rhino, but Send to RoboDK is blocked — " +
                    "the robot would crash. Re-slice after adjusting the " +
                    "build volume, the part position, or the shrinkage %.",
                offerContinue: false);
        }

        /// <summary>
        /// Shared modal warning dialog. Red bold "W A R N I N G !" header,
        /// the per-axis overflow description in monospace, and either
        /// {Cancel, Continue Anyway} (offerContinue=true, returns true on
        /// continue) or just {OK} (offerContinue=false, always returns true).
        /// </summary>
        private bool ShowBuildVolumeOverflowDialog(
            string bodyIntro,
            BuildVolumeCheck.Overflow overflow,
            string bodyOutro,
            bool offerContinue)
        {
            // Layout strategy:
            //   - Set explicit Width on the wrap-mode labels so they wrap
            //     at a known width (no monitor-wide blowup from long
            //     single-line text).
            //   - Set an explicit Size that fits the worst-case content
            //     (4-axis overflow + longest outro) plus the button row.
            //     Auto-height didn't reliably fit the buttons on Windows
            //     with the wrapped labels — they got clipped at the bottom.
            //   - Resizable=false so the size is exact.
            const int LabelWrapWidth = 400;

            var dlg = new Dialog<bool>
            {
                Title = "CCL_Clay3DP — Build volume exceeded",
                // 400 fits the worst case (4-axis overflow + longest
                // outro + button row + padding) with the null soaker
                // keeping buttons at natural height.
                Size = new Size(460, 400),
                Resizable = false,
            };

            var headerLabel = new Label
            {
                Text = "W A R N I N G !",
                TextColor = Colors.Red,
                // Fully qualify Font — Rhino.DocObjects.Font and
                // Eto.Drawing.Font are both in scope from the file's
                // using directives.
                Font = new Eto.Drawing.Font(SystemFont.Bold, 16),
                TextAlignment = TextAlignment.Center,
            };

            var introLabel = new Label
            {
                Text = bodyIntro,
                Wrap = WrapMode.Word,
                Width = LabelWrapWidth,
            };

            var overflowLabel = new Label
            {
                Text = overflow.Describe(),
                Font = new Eto.Drawing.Font(FontFamilies.Monospace, 10),
            };

            var outroLabel = new Label
            {
                Text = bodyOutro,
                Wrap = WrapMode.Word,
                Width = LabelWrapWidth,
            };

            TableLayout buttonRow;
            if (offerContinue)
            {
                var continueBtn = new Button { Text = "Continue anyway" };
                continueBtn.Click += (s, e) => dlg.Close(true);
                var cancelBtn = new Button { Text = "Cancel" };
                cancelBtn.Click += (s, e) => dlg.Close(false);

                dlg.DefaultButton = cancelBtn;  // safe default — Enter cancels
                dlg.AbortButton = cancelBtn;    // Esc cancels

                buttonRow = new TableLayout
                {
                    Spacing = new Size(8, 0),
                    Rows = { new TableRow(null, cancelBtn, continueBtn) },
                };
            }
            else
            {
                var okBtn = new Button { Text = "OK" };
                okBtn.Click += (s, e) => dlg.Close(true);

                dlg.DefaultButton = okBtn;
                dlg.AbortButton = okBtn;

                buttonRow = new TableLayout
                {
                    Spacing = new Size(8, 0),
                    Rows = { new TableRow(null, okBtn) },
                };
            }

            // Layout note: Eto's TableLayout scales the LAST non-null
            // row vertically by default — without the null spacer, the
            // button row absorbed all leftover space and the buttons
            // ballooned to ~100px tall. The null row sits between the
            // outro and the buttons so it eats the slack instead, and
            // buttons stay at their natural height.
            dlg.Content = new TableLayout
            {
                Spacing = new Size(0, 12),
                Padding = new Padding(20),
                Rows =
                {
                    new TableRow(headerLabel),
                    new TableRow(introLabel),
                    new TableRow(overflowLabel),
                    new TableRow(outroLabel),
                    null, // soak leftover vertical space
                    new TableRow(buttonRow),
                },
            };

            // ShowModal() alone does NOT center on the screen — Eto on
            // Windows lets the OS use a default placement (typically near
            // the parent app's top-left). We compute the centered position
            // explicitly from the active screen's bounds.
            CenterOnActiveScreen(dlg);
            return dlg.ShowModal();
        }

        /// <summary>
        /// Position a window at the centre of whichever screen the mouse
        /// cursor is currently on (so multi-monitor users get the dialog
        /// where they're looking, not on the primary display). Falls back
        /// to PrimaryScreen if Mouse.Position isn't on any known screen.
        /// Defensive: any layout math failure just leaves the dialog at
        /// its default location rather than blocking the popup.
        ///
        /// Convention for ALL panel-originated popups: never anchor to
        /// the docked panel — when it's narrow or against a screen edge
        /// the popup gets clipped. For custom Dialog&lt;T&gt;, call this
        /// helper before ShowModal() (no parent). For MessageBox.Show,
        /// drop the parent argument — the OS-default placement on
        /// Windows centres it on screen for parentless calls.
        /// </summary>
        private static void CenterOnActiveScreen(Window dlg)
        {
            try
            {
                Eto.Forms.Screen screen = null;
                try { screen = Eto.Forms.Screen.FromPoint(Mouse.Position); }
                catch { /* FromPoint not available on all platforms */ }
                if (screen == null) screen = Eto.Forms.Screen.PrimaryScreen;
                if (screen == null) return;

                var bounds = screen.Bounds;
                var size = dlg.Size;
                // Fully qualify Point — Eto.Drawing.Point and
                // Rhino.Geometry.Point both in scope from the file's
                // using directives.
                dlg.Location = new Eto.Drawing.Point(
                    (int)(bounds.X + (bounds.Width - size.Width) / 2),
                    (int)(bounds.Y + (bounds.Height - size.Height) / 2));
            }
            catch
            {
                // Centering is cosmetic — never let it throw.
            }
        }

        // ---------------------------------------------------------------
        // Auto-rebuild when the source geometry is transformed in the
        // Rhino doc (move, scale, rotate, gumball drag, etc.).
        //
        // RhinoDoc.ReplaceRhinoObject fires synchronously on every
        // object modification, including all transforms. The handler
        // filters for our cached _lastRawGeometry.SourceObjectId and
        // kicks the debounce timer; the debounce coalesces a continuous
        // drag into a single rebuild on release. The suppress flag
        // ignores the event WE triggered ourselves via RunPipeline's
        // auto-translate (which would otherwise loop infinitely).
        //
        // Skip cases:
        //   - No cached geometry yet (user hasn't sliced once)
        //   - Layer + Bracing combo (interactive prompts make auto-
        //     rebuild on every drag intolerable — same fallback as the
        //     settings-change rebuild)
        // ---------------------------------------------------------------

        private void OnRhinoObjectReplaced(object sender, RhinoReplaceObjectEventArgs e)
        {
            if (_suppressGeometryChangeRebuild) return;
            if (_lastRawGeometry == null) return;
            if (_lastRawGeometry.SourceObjectId == Guid.Empty) return;
            if (e.ObjectId != _lastRawGeometry.SourceObjectId) return;

            // Restart the debounce. Any in-flight pending rebuild gets
            // pushed back another 500 ms — this is what coalesces a
            // continuous drag into one rebuild.
            _rebuildDebounce.Stop();
            _rebuildDebounce.Start();
        }

        private void OnRebuildDebounceElapsed(object sender, EventArgs e)
        {
            _rebuildDebounce.Stop();

            // Layer + Bracing combo prompts mid-slice; auto-rebuilding
            // on every geometry edit would spam those prompts. Same
            // fallback policy as the settings-change rebuild.
            bool layerBracing = !_settings.Helix.SpiralSlice
                && _settings.Helix.OuterWallBracing;
            if (layerBracing)
            {
                SetStatus("Geometry changed — click Slice to regenerate (Layer + Bracing needs interactive prompts)");
                return;
            }

            // Fetch the post-edit geometry from the doc. The user may
            // have deleted or replaced it with an unsupported type;
            // bail gracefully if we can't recover a usable selection.
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || _lastRawGeometry == null) return;
            var docObj = doc.Objects.Find(_lastRawGeometry.SourceObjectId);
            if (docObj == null || !docObj.IsValid)
            {
                // Object was deleted — clear the cache so we don't keep
                // listening to a phantom ID, and let the user click
                // Slice when they're ready with new geometry.
                _lastRawGeometry = null;
                SetStatus("Source geometry deleted — click Slice with new geometry");
                return;
            }

            var fresh = new GeometrySelection
            {
                SourceObjectId = _lastRawGeometry.SourceObjectId,
            };
            if (docObj.Geometry is Brep brep)
                fresh.Brep = brep.DuplicateBrep();
            else if (docObj.Geometry is Surface surf)
                fresh.Brep = surf.ToBrep();
            else if (docObj.Geometry is Mesh mesh)
                fresh.Mesh = mesh.DuplicateMesh();
            else
            {
                SetStatus("Source geometry replaced with unsupported type — click Slice with new geometry");
                return;
            }

            SetStatus("Geometry changed — regenerating...");
            RhinoApp.Wait();
            RunPipeline(fresh);
        }

        private void SetStatus(string message)
        {
            _statusLabel.Text = $"Status: {message}";
            RhinoApp.WriteLine($"[CCL_Clay3DP] {message}");
        }

        // Append the most recent slice's translation note to a status, then
        // consume it so it isn't repeated on later (non-slice) status lines.
        private string WithTranslationNote(string baseMsg)
        {
            string note = _lastTranslationNote;
            _lastTranslationNote = null;
            return string.IsNullOrEmpty(note) ? baseMsg : $"{baseMsg} — {note}";
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
