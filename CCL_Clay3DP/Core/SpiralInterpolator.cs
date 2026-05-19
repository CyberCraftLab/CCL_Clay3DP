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
using Rhino.Geometry.Intersect;

namespace CCL_Clay3DP.Core
{
    /// <summary>
    /// Two-step spiral toolpath construction:
    ///   1. Build one continuous on-surface spiral curve whose helical
    ///      pitch is the user's LayerHeight (one full revolution per
    ///      LayerHeight of Z). Surface fidelity comes from re-slicing
    ///      the source brep/mesh at a fine Z pitch; angular fidelity
    ///      comes from ray-casting each contour at the target spiral
    ///      angle (which works correctly on non-convex / wavy contours,
    ///      unlike arc-length parameter sampling).
    ///   2. Walk that spiral curve from bottom to top at the user's
    ///      FrameSpacingMm. The resulting points are uniformly spaced
    ///      along arc length, on-surface, and Z-monotonic.
    /// </summary>
    public static class SpiralInterpolator
    {
        /// <summary>
        /// Density of intermediate spiral sampling relative to the output
        /// frame spacing. Eight intermediate samples between every two
        /// output frames keeps the polyline scaffold faithful to the
        /// contour shape — at 1× the output sampling the scaffold would
        /// cut corners on curvy contours and the resampled points would
        /// inherit those shortcuts.
        /// </summary>
        private const int IntermediateDensityFactor = 8;

        /// <summary>
        /// Floor on dense samples per contour pair regardless of perimeter.
        /// Below this the polyline can't represent a turn smoothly even
        /// on very small parts.
        /// </summary>
        private const int MinSamplesPerContourPair = 4;

        /// <summary>
        /// Default Z pitch for the internal fine-slicing pass. Independent
        /// of the user's print layer height — fine slicing improves path
        /// fidelity to the source surface without changing what the printer
        /// deposits.
        /// </summary>
        public const double DefaultPathSlicePitchMm = 0.5;

        /// <summary>
        /// Build a spiral toolpath whose helical pitch equals
        /// <paramref name="layerHeightMm"/> (one full revolution per
        /// LayerHeight of Z rise) and whose consecutive output points are
        /// spaced by <paramref name="frameSpacingMm"/> along the spiral's
        /// arc length. When a brep or mesh is supplied, the interpolator
        /// re-slices internally at <paramref name="pathSlicePitchMm"/>
        /// for surface fidelity.
        /// </summary>
        /// <param name="contours">Closed curves sorted bottom to top.
        /// Used as a Z-range source and as the working contour list
        /// when brep/mesh re-slicing isn't possible.</param>
        /// <param name="frameSpacingMm">Arc-length spacing between
        /// consecutive output points. Must be &gt; 0.</param>
        /// <param name="layerHeightMm">Helical pitch of the spiral: one
        /// full turn per this much Z rise. Must be &gt; 0.</param>
        /// <param name="startAngleDegrees">Angle in degrees at which the
        /// spiral starts on the bottom contour, measured from the +X
        /// direction around the centroid (0 = +X).</param>
        /// <param name="ccw">True for counter-clockwise winding viewed
        /// from +Z, false for clockwise.</param>
        /// <param name="brep">Optional brep source for fine re-slicing.</param>
        /// <param name="mesh">Optional mesh source for fine re-slicing
        /// (also fallback when brep slicing returns null).</param>
        /// <param name="pathSlicePitchMm">Internal fine-slice Z pitch.</param>
        public static List<Point3d> Interpolate(
            List<Curve> contours,
            double frameSpacingMm,
            double layerHeightMm,
            double startAngleDegrees,
            bool ccw,
            Brep brep = null,
            Mesh mesh = null,
            double pathSlicePitchMm = DefaultPathSlicePitchMm,
            Action<double> progress = null)
        {
            if (contours.Count < 2)
                throw new Exception("Need at least 2 contours to interpolate");
            if (frameSpacingMm <= 0.0)
                throw new ArgumentException("frameSpacingMm must be > 0", nameof(frameSpacingMm));
            if (layerHeightMm <= 0.0)
                throw new ArgumentException("layerHeightMm must be > 0", nameof(layerHeightMm));

            // Step A: build one continuous spiral curve.

            // A1. Fine-slice the source so each contour sits on the actual
            // surface. Falls back to the input contours when no source is
            // available or fine slicing wouldn't be a refinement.
            var workingContours = RefineContoursIfPossible(
                contours, brep, mesh, pathSlicePitchMm);

            // A2. Precompute centroids once. Every dense sample ray-casts
            // from its contour's centroid at the target angle, so angular
            // alignment stays consistent across contours regardless of
            // shape — no seam alignment or winding fix-up needed.
            var centroids = ComputeCentroids(workingContours);

            // A3. Sample the on-surface spiral. Spiral angle at height z
            // is startAngle ± 2π · (z − zMin) / LayerHeight (sign from
            // CCW/CW), so one full turn always corresponds to exactly
            // LayerHeight of Z rise.
            double startAngleRad = startAngleDegrees * Math.PI / 180.0;
            var dense = BuildSpiralSamples(workingContours, centroids,
                layerHeightMm, frameSpacingMm, startAngleRad, ccw, progress);
            if (dense.Count < 2) return dense;

            // A4. Connect samples with a polyline. PolylineCurve (not a
            // degree-3 fit) keeps the curve on the linear blend of our
            // on-surface samples — a NURBS fit would smooth between them
            // and overshoot the surface.
            var spiralCurve = new PolylineCurve(new Polyline(dense));

            // Step B: walk the spiral curve from bottom to top at the
            // user's frame spacing.
            return ResampleByArcLength(spiralCurve, frameSpacingMm);
        }

        /// <summary>
        /// Visualization curve through the final toolpath points. Polyline
        /// (not NURBS) so what the user sees matches what the robot receives.
        /// </summary>
        public static Curve CreateSpiralCurve(List<Point3d> points)
        {
            if (points == null || points.Count < 2) return null;
            return new PolylineCurve(new Polyline(points));
        }

        /// <summary>
        /// Re-slice the source brep/mesh at <paramref name="pitchMm"/>
        /// between the Z range of <paramref name="contours"/>. Returns the
        /// original list unchanged when refining isn't a fidelity gain.
        /// </summary>
        private static List<Curve> RefineContoursIfPossible(
            List<Curve> contours, Brep brep, Mesh mesh, double pitchMm)
        {
            if (brep == null && mesh == null) return contours;
            if (pitchMm <= 0.0) return contours;

            double zMin = ContourZ(contours[0]);
            double zMax = ContourZ(contours[contours.Count - 1]);
            if (zMax - zMin <= pitchMm) return contours;

            double existingPitch = (zMax - zMin) / (contours.Count - 1);
            if (pitchMm >= existingPitch) return contours;

            var refined = new List<Curve>();
            for (double z = zMin; z <= zMax + 1e-6; z += pitchMm)
            {
                Curve sliced = null;
                if (brep != null)
                    sliced = ContourSlicer.SliceBrepAt(brep, z);
                if (sliced == null && mesh != null)
                    sliced = ContourSlicer.SliceMeshAt(mesh, z);
                if (sliced != null)
                    refined.Add(sliced);
            }

            return refined.Count >= 2 ? refined : contours;
        }

        /// <summary>
        /// Generate dense on-surface samples along the spiral. For each
        /// adjacent contour pair, emit K samples whose Z rises linearly
        /// across the pair and whose angular position is computed from
        /// absolute Z and the helical pitch — so one full turn always
        /// corresponds to LayerHeight of Z, regardless of whether a
        /// contour pair spans a full turn (print pitch) or a fraction
        /// of one (fine slicing).
        ///
        /// Within a pair, each sample is the chord blend between the two
        /// bracketing contours, each ray-cast at the same angle from its
        /// centroid. Chord deviation from the surface shrinks with the
        /// contour spacing — fine slicing keeps it small even on wavy
        /// organic meshes.
        /// </summary>
        private static List<Point3d> BuildSpiralSamples(
            List<Curve> contours, List<Point3d> centroids,
            double layerHeightMm, double frameSpacingMm,
            double startAngleRad, bool ccw, Action<double> progress)
        {
            var dense = new List<Point3d>();
            int totalPairs = contours.Count - 1;
            if (totalPairs < 1) return dense;

            double zMin = ContourZ(contours[0]);
            double zMax = ContourZ(contours[contours.Count - 1]);

            // Density: each pair gets enough samples that adjacent dense
            // points are ~frameSpacing / 8 apart along the longest
            // perimeter. Scales with the angular range each pair covers,
            // so a full-turn pair gets the full count and a 1/8-turn
            // pair gets 1/8 the count.
            double maxPerimeter = 0.0;
            foreach (var c in contours)
            {
                double len = c.GetLength();
                if (len > maxPerimeter) maxPerimeter = len;
            }
            double avgPairPitch = (zMax - zMin) / totalPairs;
            double turnFractionPerPair = avgPairPitch / layerHeightMm;
            double targetSpacingMm = frameSpacingMm / IntermediateDensityFactor;
            int samplesPerPair = Math.Max(MinSamplesPerContourPair,
                (int)Math.Ceiling(maxPerimeter * turnFractionPerPair / targetSpacingMm));

            // Per-contour bbox diagonal cached so SampleByAngle doesn't
            // recompute it on every sample.
            var rayLengths = new double[contours.Count];
            for (int i = 0; i < contours.Count; i++)
            {
                var bb = contours[i].GetBoundingBox(true);
                rayLengths[i] = bb.IsValid ? bb.Diagonal.Length * 2.0 + 100.0 : 1000.0;
            }

            double windSign = ccw ? 1.0 : -1.0;

            for (int pair = 0; pair < totalPairs; pair++)
            {
                progress?.Invoke(0.03 + (double)pair / totalPairs * 0.02);

                var lower = contours[pair];
                var upper = contours[pair + 1];
                double zLo = ContourZ(lower);
                double zHi = ContourZ(upper);

                for (int k = 0; k < samplesPerPair; k++)
                {
                    double zFrac = (double)k / samplesPerPair;
                    double z = zLo + (zHi - zLo) * zFrac;
                    double angleRad = SpiralAngle(z, zMin, layerHeightMm, startAngleRad, windSign);

                    Point3d? ptLo = SampleByAngle(lower, centroids[pair], rayLengths[pair], angleRad);
                    Point3d? ptHi = SampleByAngle(upper, centroids[pair + 1], rayLengths[pair + 1], angleRad);
                    if (!ptLo.HasValue || !ptHi.HasValue) continue;

                    double x = ptLo.Value.X + (ptHi.Value.X - ptLo.Value.X) * zFrac;
                    double y = ptLo.Value.Y + (ptHi.Value.Y - ptLo.Value.Y) * zFrac;
                    dense.Add(new Point3d(x, y, z));
                }
            }

            // Finish on the top contour at its spiral angle so the curve
            // terminates on the actual surface, not mid-blend.
            int topIdx = contours.Count - 1;
            double angleTop = SpiralAngle(zMax, zMin, layerHeightMm, startAngleRad, windSign);
            var topPt = SampleByAngle(contours[topIdx], centroids[topIdx],
                rayLengths[topIdx], angleTop);
            if (topPt.HasValue) dense.Add(topPt.Value);

            return dense;
        }

        private static double SpiralAngle(double z, double zMin, double layerHeightMm,
            double startAngleRad, double windSign)
        {
            double turnFrac = (z - zMin) / layerHeightMm;
            return startAngleRad + windSign * turnFrac * 2.0 * Math.PI;
        }

        /// <summary>
        /// Find the outermost intersection of a ray from <paramref name="centroid"/>
        /// at <paramref name="angleRad"/> with the contour. "Outermost" is the
        /// farthest hit along the ray, which is the surface point in that
        /// angular direction even when the contour is non-convex and the
        /// ray would otherwise stab inward concavities first.
        /// </summary>
        private static Point3d? SampleByAngle(Curve contour, Point3d centroid,
            double rayLength, double angleRad)
        {
            var dir = new Vector3d(Math.Cos(angleRad), Math.Sin(angleRad), 0);
            var rayEnd = centroid + dir * rayLength;
            rayEnd = new Point3d(rayEnd.X, rayEnd.Y, centroid.Z);
            var ray = new LineCurve(centroid, rayEnd);

            var events = Intersection.CurveCurve(ray, contour, 0.001, 0.001);
            if (events == null || events.Count == 0) return null;

            double farthestParam = double.MinValue;
            Point3d best = Point3d.Unset;
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (!ev.IsPoint) continue;
                if (ev.ParameterA > farthestParam)
                {
                    farthestParam = ev.ParameterA;
                    best = ev.PointA;
                }
            }
            return best.IsValid ? best : (Point3d?)null;
        }

        private static List<Point3d> ComputeCentroids(List<Curve> contours)
        {
            var list = new List<Point3d>(contours.Count);
            foreach (var c in contours)
            {
                var amp = AreaMassProperties.Compute(c);
                if (amp != null)
                {
                    // Pin centroid to the contour's Z plane — AreaMassProperties
                    // can drift by float noise.
                    var ctr = amp.Centroid;
                    list.Add(new Point3d(ctr.X, ctr.Y, c.PointAtStart.Z));
                }
                else
                {
                    // Degenerate contour: bbox center is the safest fallback.
                    var bb = c.GetBoundingBox(true);
                    list.Add(bb.IsValid ? bb.Center : c.PointAtStart);
                }
            }
            return list;
        }

        /// <summary>
        /// Walk a curve from start to end at uniform arc-length intervals.
        /// Always appends the curve end as the last point so the toolpath
        /// terminates at the top of the part rather than at the last
        /// full-spacing tick.
        /// </summary>
        private static List<Point3d> ResampleByArcLength(Curve curve, double spacingMm)
        {
            var pts = new List<Point3d>();
            double total = curve.GetLength();
            if (total <= 0.0) return pts;

            if (total <= spacingMm)
            {
                pts.Add(curve.PointAtStart);
                pts.Add(curve.PointAtEnd);
                return pts;
            }

            double[] ts = curve.DivideByLength(spacingMm, true);
            if (ts != null)
            {
                foreach (var t in ts)
                    pts.Add(curve.PointAt(t));
            }

            Point3d end = curve.PointAtEnd;
            if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(end) > 1e-3)
                pts.Add(end);

            return pts;
        }

        private static double ContourZ(Curve contour) => contour.PointAtStart.Z;
    }
}
