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
using Rhino.Geometry;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Core
{
    /// <summary>
    /// Per-layer breakdown of base output. The panel uses these to bake
    /// visualizations onto dedicated Rhino sublayers and to populate the
    /// SpiralResult fields that drive the RoboDK frame stream.
    /// </summary>
    public class BaseResult
    {
        // Skirt at z=0, one loop offset outward from the base footprint.
        // Matches existing SkirtBuilder behavior — the only difference
        // when base is enabled is that the skirt is sourced from the
        // base footprint rather than the part's lowest contour (the
        // curves are identical pre-shift; this is a relabeling).
        public Curve SkirtCurve { get; set; }
        public List<Plane> SkirtFrames { get; set; } = new List<Plane>();

        // One contour curve per base layer, at world Z = i · LayerHeight
        // for i in 0..LayerCount-1. Same shape as the part's lowest
        // contour, just stacked.
        public List<Curve> ContourCurves { get; set; } = new List<Curve>();

        // One infill polyline per base layer, alternating ±45° per layer.
        // Null entries are possible if a layer's infill couldn't be
        // generated (e.g., scan line returned no segments) — the bake
        // step skips nulls.
        public List<Curve> InfillCurves { get; set; } = new List<Curve>();

        // Concatenated frames in print order:
        //   layer 0 contour → layer 0 infill → layer 1 contour → layer 1
        //   infill → … → layer (N-1) contour → layer (N-1) infill
        // Skirt is NOT included here — the panel concatenates skirt +
        // base + part separately, mirroring how skirt + part is
        // concatenated when base is disabled.
        public List<Plane> Frames { get; set; } = new List<Plane>();

        // Echo of the input so the panel knows how far to shift the part.
        // Top of the base sits at LayerCount · LayerHeight; the part
        // body starts there.
        public int LayerCount { get; set; }
        public double LayerHeight { get; set; }
        public double TopZ => LayerCount * LayerHeight;
    }

    /// <summary>
    /// Builds the multi-layer base printed before a closed-loop / vase
    /// part (Issue #10). Slice 1 added the settings; this slice (2+3
    /// fused per project decision) adds the toolpath generation:
    /// skirt + N × (contour + 45° cross-hatch infill).
    ///
    /// Print order is [skirt, base layers, part]. The base sits at
    /// world Z 0..N·h; the caller is responsible for translating the
    /// part body up by N·h. All base frames carry +Z world normal —
    /// the build plate stays flat under the base regardless of the
    /// SpiralFollowsCurveNormal setting (which only applies to the
    /// part body).
    /// </summary>
    public static class BaseBuilder
    {
        /// <summary>
        /// Multiplier applied to the bead diameter to derive the
        /// infill line spacing. 1.5 gives ~67% nominal infill density
        /// — enough mechanical interlock between layers without the
        /// over-extrusion you'd get at 1.0× (100%).
        /// </summary>
        public const double LineSpacingBeadMultiplier = 1.5;

        /// <summary>
        /// Build the full base from the part's lowest cross-section.
        /// The contour is normalized to z=0 internally; the caller may
        /// pass a contour at any Z.
        /// </summary>
        /// <param name="lowestContour">Closed planar curve sampled
        /// from the part's lowest sliceable height.</param>
        /// <param name="settings">User base config (already validated
        /// in the dialog; this method clamps defensively in case
        /// settings were imported from JSON with out-of-range values).</param>
        /// <param name="layerHeight">Layer height in mm. Mirrors
        /// HelixParameters.LayerHeight — base layers print at the same
        /// pitch as the part body.</param>
        /// <param name="beadDiameter">Material bead diameter in mm.
        /// Drives infill line spacing.</param>
        /// <param name="frameSpacingMm">Target arc-length spacing between
        /// frames on the skirt and on each base layer contour, in mm.
        /// Mirrors HelixParameters.FrameSpacingMm so the base prints at
        /// the same bead density as the part body (Issue #22).</param>
        /// <returns>BaseResult with frames and bake geometry. Returns
        /// an empty result (LayerCount=0, empty lists) if the input
        /// contour is unusable — caller treats that as "no base".</returns>
        public static BaseResult Build(
            Curve lowestContour,
            BaseSettings settings,
            double layerHeight,
            double beadDiameter,
            double frameSpacingMm)
        {
            var result = new BaseResult { LayerHeight = layerHeight };

            if (settings == null || !settings.EnableBase) return result;
            if (lowestContour == null || !lowestContour.IsClosed) return result;
            if (layerHeight <= 0.0 || beadDiameter <= 0.0) return result;

            // Clamp imported values defensively. The dialog also clamps
            // 2..10 but a hand-edited settings.json could bypass that.
            int n = settings.LayerCount;
            if (n < 2) n = 2;
            if (n > 10) n = 10;
            result.LayerCount = n;

            double lineSpacing = beadDiameter * LineSpacingBeadMultiplier;

            // Normalize the footprint to z=0 — the part's lowest contour
            // is sampled at bbox.Min.Z + 0.01 (per ContourSlicer), and
            // user geometry may sit at any Z. We want the bottom base
            // layer at world z=0 (build plate).
            var footprint = lowestContour.DuplicateCurve();
            var fbbox = footprint.GetBoundingBox(true);
            if (fbbox.IsValid && Math.Abs(fbbox.Min.Z) > 1e-6)
                footprint.Translate(0.0, 0.0, -fbbox.Min.Z);

            // Skirt at z=0 — first thing the robot prints. Reuses the
            // existing builder so the offset distance and sampling
            // convention match the no-base case exactly.
            result.SkirtCurve = SkirtBuilder.BuildSkirt(footprint);
            if (result.SkirtCurve != null)
            {
                result.SkirtFrames = SkirtBuilder.SampleSkirtFrames(
                    result.SkirtCurve, frameSpacingMm);
            }

            for (int i = 0; i < n; i++)
            {
                double z = i * layerHeight;

                // Contour: footprint translated up to this layer's Z.
                var contour = footprint.DuplicateCurve();
                contour.Translate(0.0, 0.0, z);
                result.ContourCurves.Add(contour);
                result.Frames.AddRange(SampleContourFrames(contour, frameSpacingMm));

                // Infill: alternating ±45 per layer. Even layers (i=0,2,…)
                // at +45°; odd layers at -45°. Stacked, this gives a
                // 90° crosshatch lattice — the structurally preferred
                // pattern per the project owner. InfillGenerator returns
                // a list of polylines because non-convex contours force
                // the snake path to break at concavities (otherwise the
                // straight connector across a void would leak material
                // outside the contour).
                double angle = (i % 2 == 0) ? 45.0 : -45.0;
                var infillSegments = InfillGenerator.GenerateCrossHatch(
                    footprint, z, angle, lineSpacing);

                foreach (var segCurve in infillSegments)
                {
                    if (segCurve == null) continue;
                    result.InfillCurves.Add(segCurve);
                    result.Frames.AddRange(SampleInfillFrames(segCurve));
                }
            }

            return result;
        }

        /// <summary>
        /// Sample a closed planar contour into frames suitable for the
        /// robot. Mirrors SkirtBuilder.SampleSkirtFrames: uniform arc
        /// length at frameSpacingMm (sample count derived from perimeter
        /// per Issue #22), last sample coincides with first to close the
        /// loop, frame YAxis = +Z so the build plate stays flat regardless
        /// of the SpiralFollowsCurveNormal setting.
        /// </summary>
        private static List<Plane> SampleContourFrames(Curve contour, double frameSpacingMm)
        {
            var frames = new List<Plane>();
            if (contour == null || frameSpacingMm <= 0.0) return frames;

            double perimeter = contour.GetLength();
            if (perimeter <= 0.0) return frames;

            int sampleCount = Math.Max(4, (int)Math.Ceiling(perimeter / frameSpacingMm));

            double[] ts = contour.DivideByCount(sampleCount, true);
            if (ts == null || ts.Length == 0) return frames;

            foreach (var t in ts)
            {
                var pt = contour.PointAt(t);
                var tan = contour.TangentAt(t);
                var tanXY = new Vector3d(tan.X, tan.Y, 0.0);
                if (!tanXY.Unitize()) tanXY = Vector3d.XAxis;
                frames.Add(new Plane(pt, tanXY, Vector3d.ZAxis));
            }
            return frames;
        }

        /// <summary>
        /// Sample an infill polyline into frames. Unlike the contour
        /// case we use the polyline's own vertices directly — every
        /// corner is a direction change that the robot must hit
        /// exactly, so resampling by arc length would round them off.
        /// Tangent at each vertex points to the next vertex (last
        /// vertex inherits the previous segment's direction).
        /// </summary>
        private static List<Plane> SampleInfillFrames(Curve infill)
        {
            var frames = new List<Plane>();
            if (infill == null) return frames;

            var pc = infill as PolylineCurve;
            if (pc == null) return frames;

            int count = pc.PointCount;
            if (count < 2) return frames;

            for (int i = 0; i < count; i++)
            {
                Point3d pt = pc.Point(i);
                Vector3d tan;
                if (i < count - 1)
                    tan = pc.Point(i + 1) - pt;
                else
                    tan = pt - pc.Point(i - 1);

                var tanXY = new Vector3d(tan.X, tan.Y, 0.0);
                if (!tanXY.Unitize()) tanXY = Vector3d.XAxis;
                frames.Add(new Plane(pt, tanXY, Vector3d.ZAxis));
            }
            return frames;
        }
    }
}
