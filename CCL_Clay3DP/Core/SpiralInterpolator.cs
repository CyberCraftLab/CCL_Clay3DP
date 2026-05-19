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

namespace CCL_Clay3DP.Core
{
    /// <summary>
    /// Two-step spiral toolpath construction:
    ///   1. Build one continuous spiral curve. Helical pitch = LayerHeight
    ///      (one revolution per LayerHeight of Z). Surface fidelity comes
    ///      from re-slicing the source brep/mesh at a fine pitch; angular
    ///      consistency across layers comes from aligning each contour's
    ///      seam to the +X-most point as seen from a shared centroid.
    ///      Chord-blend samples are laterally projected onto the source
    ///      surface where the surface is steep enough that the projection
    ///      stays within LayerHeight/2 in Z.
    ///   2. Walk the spiral curve from bottom to top at the user's
    ///      FrameSpacingMm. The output points are uniformly spaced along
    ///      arc length and Z-monotonic.
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
        /// Distance, in mm, used to construct seam-alignment targets far
        /// outside any plausible contour. Curve.ClosestPoint to a target
        /// this far in seamDir resolves to the contour's extreme point in
        /// that direction, which is what we want for the seam.
        /// </summary>
        private const double SeamRayDistanceMm = 10000.0;

        /// <summary>
        /// Spatial tolerance for de-duplicating coincident endpoints when
        /// resampling closed curves — DivideByLength can return both T0
        /// and T1 on closed curves, which collapse to the same XYZ.
        /// </summary>
        private const double PointDedupTolMm = 1e-3;

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

            // A2. Align winding so walking parameter t forward = chosen
            // winding direction (CCW/CW), and align seams via a SHARED
            // centroid (mean of all per-layer centroids). With seams
            // angularly aligned, parameter t=0 maps to the same angular
            // column on every contour — arc-length sampling at the same
            // t on adjacent contours yields chord blends that follow the
            // actual perimeter, even for multi-lobe / wavy shapes where
            // ray-cast-from-centroid would short-circuit across the part.
            AlignContourDirections(workingContours, ccw);
            Point3d sharedCentroid = ComputeSharedCentroid(workingContours);
            AlignSeamPoints(workingContours, sharedCentroid, startAngleDegrees);

            // A3. Sample the on-surface spiral. Each sample's parameter on
            // the contour is t = ((z − zMin) / LayerHeight) mod 1 — one
            // full revolution per LayerHeight of Z rise, regardless of
            // how many fine slices we have. Where the source brep/mesh
            // is available, each chord-blend point is laterally projected
            // onto the surface (constrained to small Z deltas so the
            // spiral stays monotonic in Z even on near-horizontal regions).
            var dense = BuildSpiralSamples(workingContours,
                layerHeightMm, frameSpacingMm, brep, mesh, progress);
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
        /// between the Z range of <paramref name="contours"/>. Always
        /// returns a fresh list — even on the no-refinement paths it
        /// copies the input, because the caller mutates the returned
        /// list (winding flips, seam rotation) and must not corrupt the
        /// caller's contour list.
        /// </summary>
        private static List<Curve> RefineContoursIfPossible(
            List<Curve> contours, Brep brep, Mesh mesh, double pitchMm)
        {
            if (brep == null && mesh == null) return new List<Curve>(contours);
            if (pitchMm <= 0.0) return new List<Curve>(contours);

            double zMin = ContourZ(contours[0]);
            double zMax = ContourZ(contours[contours.Count - 1]);
            if (zMax - zMin <= pitchMm) return new List<Curve>(contours);

            double existingPitch = (zMax - zMin) / (contours.Count - 1);
            if (pitchMm >= existingPitch) return new List<Curve>(contours);

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

            return refined.Count >= 2 ? refined : new List<Curve>(contours);
        }

        /// <summary>
        /// Generate dense on-surface samples along the spiral. For each
        /// adjacent contour pair, emit K samples whose Z rises linearly
        /// across the pair and whose contour parameter t advances such
        /// that one full revolution corresponds to exactly LayerHeight
        /// of Z rise (helical pitch). Within a pair, each sample is the
        /// chord blend between the two bracketing contours, each
        /// sampled at the same arc-length parameter t.
        ///
        /// Arc-length parameterization walks the actual perimeter — so
        /// multi-lobe / non-convex contours are traced correctly. Seam
        /// alignment (done earlier, in <see cref="AlignSeamPoints"/>)
        /// guarantees that the same t lands at the same angular column
        /// on every contour, so chord blends stay close to the surface.
        /// Chord deviation shrinks with contour spacing — fine slicing
        /// keeps it small even on wavy organic meshes.
        /// </summary>
        private static List<Point3d> BuildSpiralSamples(
            List<Curve> contours,
            double layerHeightMm, double frameSpacingMm,
            Brep brep, Mesh mesh,
            Action<double> progress)
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

            // Constrained projection threshold: how far the closest surface
            // point's Z can differ from the chord-blend Z before we reject
            // the projection. Half the user's LayerHeight is the safe band
            // — closer than that and the surface is steep enough to project
            // onto without crossing into a different layer; further than
            // that and the projection is on a near-horizontal section that
            // would yank the point onto a different layer entirely.
            double projectionZTolerance = layerHeightMm * 0.5;

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

                    double turns = (z - zMin) / layerHeightMm;
                    double t = turns - Math.Floor(turns);

                    Point3d ptLo = SampleAtArcLengthParameter(lower, t);
                    Point3d ptHi = SampleAtArcLengthParameter(upper, t);

                    double x = ptLo.X + (ptHi.X - ptLo.X) * zFrac;
                    double y = ptLo.Y + (ptHi.Y - ptLo.Y) * zFrac;
                    var pt = new Point3d(x, y, z);

                    // Constrained lateral projection: where the surface is
                    // steep, the closest surface point is at nearly the same
                    // Z as our chord — adopt its XY so the bead hugs the
                    // actual wall. Where the surface is near-horizontal,
                    // the closest point jumps to a different layer; reject
                    // the projection and keep the chord-blend (the price of
                    // staying on a printable monotonic-Z spiral).
                    pt = LaterallyProjectIfSafe(pt, brep, mesh, projectionZTolerance);

                    dense.Add(pt);
                }
            }

            // Finish on the top contour at the spiral's terminal parameter
            // so the curve ends on the actual surface, not mid-blend.
            var top = contours[contours.Count - 1];
            double turnsTop = (zMax - zMin) / layerHeightMm;
            double tTop = turnsTop - Math.Floor(turnsTop);
            dense.Add(SampleAtArcLengthParameter(top, tTop));

            return dense;
        }

        /// <summary>
        /// If the closest point on the brep/mesh has a Z within
        /// <paramref name="zTolerance"/> of <paramref name="chord"/>, return a
        /// new point with that surface point's XY and the chord's Z. Otherwise
        /// return <paramref name="chord"/> unchanged. Z is always preserved so
        /// the spiral stays monotonic in Z; the projection only slides the
        /// point laterally onto the surface where doing so is safe.
        /// </summary>
        private static Point3d LaterallyProjectIfSafe(Point3d chord, Brep brep,
            Mesh mesh, double zTolerance)
        {
            if (brep == null && mesh == null) return chord;

            Point3d surface = Point3d.Unset;
            if (brep != null)
            {
                var cp = brep.ClosestPoint(chord);
                if (cp.IsValid) surface = cp;
            }
            if (!surface.IsValid && mesh != null)
            {
                var mp = mesh.ClosestMeshPoint(chord, 0.0);
                if (mp != null && mp.Point.IsValid) surface = mp.Point;
            }
            if (!surface.IsValid) return chord;

            if (Math.Abs(surface.Z - chord.Z) > zTolerance) return chord;
            return new Point3d(surface.X, surface.Y, chord.Z);
        }

        /// <summary>
        /// Sample a curve at a normalized arc-length parameter t ∈ [0, 1).
        /// Falls back to the curve's domain mapping if Rhino can't resolve
        /// the arc-length lookup (degenerate curves, mostly).
        /// </summary>
        private static Point3d SampleAtArcLengthParameter(Curve curve, double t)
        {
            double len = curve.GetLength();
            if (len > 0.0 && curve.LengthParameter(t * len, out double param))
                return curve.PointAt(param);
            return curve.PointAt(curve.Domain.ParameterAt(t));
        }

        /// <summary>
        /// Reverse contours whose closed-curve orientation doesn't match
        /// the requested winding, so walking parameter t forward always
        /// traces in the chosen direction (CCW or CW).
        /// </summary>
        private static void AlignContourDirections(List<Curve> contours, bool ccw)
        {
            foreach (var contour in contours)
            {
                var orientation = contour.ClosedCurveOrientation(Vector3d.ZAxis);
                bool isCCW = orientation == CurveOrientation.CounterClockwise;
                if (isCCW != ccw)
                    contour.Reverse();
            }
        }

        /// <summary>
        /// Rotate each closed contour's parameterization so t=0 lands at
        /// the +X-most point of the contour as seen from
        /// <paramref name="sharedCentroid"/>, with <paramref name="startAngleDegrees"/>
        /// rotating that reference direction. Using the SHARED centroid
        /// (not each contour's own centroid) keeps seams angularly
        /// aligned across layers even when individual contour centroids
        /// shift with the surface.
        /// </summary>
        private static void AlignSeamPoints(List<Curve> contours,
            Point3d sharedCentroid, double startAngleDegrees)
        {
            double angleRad = startAngleDegrees * Math.PI / 180.0;
            var seamDir = new Vector3d(Math.Cos(angleRad), Math.Sin(angleRad), 0);

            foreach (var contour in contours)
            {
                Point3d seamTarget = sharedCentroid + seamDir * SeamRayDistanceMm;
                seamTarget = new Point3d(seamTarget.X, seamTarget.Y, contour.PointAtStart.Z);
                if (contour.ClosestPoint(seamTarget, out double t))
                    contour.ChangeClosedCurveSeam(t);
            }
        }

        /// <summary>
        /// Mean of all contours' area-centroids, projected to z=0. Used
        /// as the shared anchor for seam alignment across layers. Falls
        /// back to each contour's bbox center when AreaMassProperties
        /// returns null. Returns Point3d.Origin when no contour has a
        /// usable centroid or bbox (degenerate input).
        ///
        /// Public so the panel can reuse it for layer-mode bracing —
        /// every consumer that wants to align angular positions across
        /// a contour stack should anchor to the same shared centroid.
        /// </summary>
        public static Point3d ComputeSharedCentroid(List<Curve> contours)
        {
            double sumX = 0.0, sumY = 0.0;
            int n = 0;
            foreach (var c in contours)
            {
                if (c == null) continue;
                Point3d centre;
                var amp = AreaMassProperties.Compute(c);
                if (amp != null)
                {
                    centre = amp.Centroid;
                }
                else
                {
                    var bb = c.GetBoundingBox(true);
                    if (!bb.IsValid) continue;
                    centre = bb.Center;
                }
                sumX += centre.X;
                sumY += centre.Y;
                n++;
            }
            return n > 0 ? new Point3d(sumX / n, sumY / n, 0.0) : Point3d.Origin;
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
            if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(end) > PointDedupTolMm)
                pts.Add(end);

            return pts;
        }

        private static double ContourZ(Curve contour) => contour.PointAtStart.Z;
    }
}
