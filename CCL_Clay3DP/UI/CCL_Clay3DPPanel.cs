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
                // Outer Wall Bracing is on), so we leave it for the user to
                // kick off with the Slice button.
                SetStatus("Settings changed — regenerating spiral...");
                RhinoApp.Wait();
                RunSliceAndBake(geometryToRebuild);
            }
            else
            {
                SetStatus("Generated layers cleared — click Slice to regenerate");
            }

            // Re-bake the visual markers regardless of whether we did an
            // auto-slice. ClearGeneratedContent above wiped them along
            // with the slice content; without these calls they would
            // stay gone until the next manual Slice click. Build Volume
            // depends only on settings (so it's always safe to re-bake);
            // Print Position depends on the last selected geometry, so
            // it's only re-baked when we still have a reference to it.
            BakeBuildVolume(doc, _settings.BuildVolume);
            if (geometryToRebuild != null)
                BakePrintPositionMarker(doc, geometryToRebuild);

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

            // Outer Wall Bracing gate — bracing's per-layer DivideByCount
            // + seam-align algorithm only behaves on ruled / extrudable
            // geometry. On free-form (sphere, organic Brep) the seam
            // wanders between layers and the bracing twists into junk.
            // Reject before mutating anything (no clear, no translate).
            if (!_settings.Helix.SpiralSlice
                && _settings.Helix.OuterWallBracing
                && !GeometryCurvature.IsRuled(selection))
            {
                MessageBox.Show(this,
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

            // Clean slate: remove any generated output from a previous run
            // (including output from the OTHER mode) so what's in Rhino
            // always reflects the current Settings choice.
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
            {
                ClearGeneratedContent(doc);
                _lastResult = null;
            }

            // Auto-translate the selection to world origin so the model,
            // the toolpath, and the build-volume box all share the same
            // frame of reference (commercial-slicer convention). The
            // user's original Rhino object physically moves — this is
            // intentional, matches PrusaSlicer / Cura / Bambu Studio
            // behavior, and the move is undoable with Ctrl+Z. Subsequent
            // slices on the same object are no-ops since the bbox
            // bottom-center is already at origin.
            double translationDistance;
            var translation = ComputePrintingTranslation(
                selection, out translationDistance);
            if (doc != null && translationDistance >= 0.001
                && selection.SourceObjectId != Guid.Empty)
            {
                doc.Objects.Transform(
                    selection.SourceObjectId, translation, true);
            }
            // Match the in-memory Brep/Mesh copy to the doc state so the
            // slicer operates on geometry at the origin.
            var printingSelection = ApplyTransform(selection, translation);
            if (doc != null)
            {
                BakePrintPositionMarker(doc, printingSelection);
                BakeBuildVolume(doc, _settings.BuildVolume);
            }
            string translationMsg = translationDistance < 0.001
                ? "Geometry already at origin"
                : $"Geometry translated {translationDistance:F1} mm to " +
                  "origin for printing";
            _lastTranslationNote = translationMsg;
            SetStatus(translationMsg);

            if (_settings.Helix.SpiralSlice)
                RunSliceAndBake(printingSelection);
            else
                RunLayerSlice(printingSelection);
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
                this,
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
