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
using System.Linq;
using Rhino.Geometry;

namespace CCL_Clay3DP.Core
{
    /// <summary>
    /// Builds a single closed skirt loop offset outward from the part's
    /// lowest contour. The skirt is the first thing the robot prints —
    /// it primes the extruder and gives a visible reference for bed
    /// alignment before the part itself starts.
    ///
    /// The offset distance is fixed (no setting): we always want a 15 mm
    /// buffer between the part outline and the skirt so the robot has
    /// clear travel space at the part's start point.
    /// </summary>
    public static class SkirtBuilder
    {
        /// <summary>
        /// Outward offset from the lowest contour, in mm. Fixed value
        /// (per Issue #8): always present, not user-configurable.
        /// </summary>
        public const double SkirtOffsetMm = 15.0;

        /// <summary>
        /// Compute the skirt curve from the lowest contour of a sliced
        /// part. Returns null if the input is unusable (null, open, or
        /// non-planar) or if Curve.Offset produces no result.
        /// </summary>
        public static Curve BuildSkirt(Curve lowestContour, double tolerance = 0.01)
        {
            if (lowestContour == null) return null;
            if (!lowestContour.IsClosed) return null;

            // Contours come from horizontal slicing so the offset plane
            // normal is +Z. Plane origin is irrelevant — Offset uses the
            // normal direction only — but we anchor at the contour's own
            // start point to keep things tidy in case Rhino logs it.
            var planeOrigin = lowestContour.PointAtStart;
            var offsetPlane = new Plane(planeOrigin, Vector3d.ZAxis);

            // Try both signed offsets. The "outward" direction depends
            // on the contour's winding (CCW vs CW); we don't want to
            // assume one. Whichever offset returns the curve with the
            // larger enclosed area is the outward one.
            var positive = SafeOffset(lowestContour, offsetPlane,
                +SkirtOffsetMm, tolerance);
            var negative = SafeOffset(lowestContour, offsetPlane,
                -SkirtOffsetMm, tolerance);

            return PickLargerArea(positive, negative);
        }

        // Curve.Offset can throw or return null on degenerate input;
        // rather than fail the whole slice, we swallow and return null
        // so the caller treats it like "no skirt produced".
        private static Curve SafeOffset(Curve curve, Plane plane,
            double distance, double tolerance)
        {
            try
            {
                var pieces = curve.Offset(plane, distance, tolerance,
                    CurveOffsetCornerStyle.Sharp);
                if (pieces == null || pieces.Length == 0) return null;
                if (pieces.Length == 1) return pieces[0];

                // Multiple pieces → join into one closed curve if we can.
                var joined = Curve.JoinCurves(pieces, tolerance);
                if (joined != null && joined.Length > 0)
                    return joined.OrderByDescending(AreaOf).First();
                return pieces.OrderByDescending(AreaOf).First();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sample the skirt curve into a list of frames the robot can
        /// follow. Sampling is by uniform arc-length at frameSpacingMm
        /// (Issue #22): the sample count is derived from the skirt's
        /// perimeter, so the bead density matches the spiral toolpath
        /// regardless of part size. The last frame duplicates the first
        /// so the loop closes back on itself when RoboDK traces the curve.
        ///
        /// Frame normals are ALWAYS +Z world (build plate up). The
        /// SpiralFollowsCurveNormal toggle does not apply to the skirt
        /// — it sits flat on the plate by construction.
        /// </summary>
        public static List<Plane> SampleSkirtFrames(Curve skirt, double frameSpacingMm)
        {
            var frames = new List<Plane>();
            if (skirt == null || frameSpacingMm <= 0.0) return frames;

            double perimeter = skirt.GetLength();
            if (perimeter <= 0.0) return frames;

            // Sample count = perimeter / spacing, but never below 4 — a
            // closed loop needs at least four corners to be meaningful.
            int sampleCount = Math.Max(4, (int)Math.Ceiling(perimeter / frameSpacingMm));

            // DivideByCount(N, true) returns N+1 parameters, with the last
            // one at curve.Domain.T1 — for a closed curve that coincides
            // spatially with the first sample, naturally closing the loop.
            double[] ts = skirt.DivideByCount(sampleCount, true);
            if (ts == null || ts.Length == 0) return frames;

            foreach (var t in ts)
            {
                var pt = skirt.PointAt(t);
                var tangent = skirt.TangentAt(t);
                if (!tangent.IsValid || tangent.IsZero)
                    tangent = Vector3d.XAxis;

                // Build a plane with X = tangent (in XY plane) and
                // Y = +Z. FrameSerializer reads frame.YAxis when
                // followCurveNormal is true, so making YAxis = +Z
                // guarantees the skirt always carries a +Z world
                // normal regardless of what the part is set to —
                // important now that skirt + part frames get
                // concatenated into one continuous curve.
                var tangentXY = new Vector3d(tangent.X, tangent.Y, 0.0);
                if (!tangentXY.Unitize()) tangentXY = Vector3d.XAxis;
                frames.Add(new Plane(pt, tangentXY, Vector3d.ZAxis));
            }
            return frames;
        }

        private static Curve PickLargerArea(Curve a, Curve b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return AreaOf(a) >= AreaOf(b) ? a : b;
        }

        private static double AreaOf(Curve c)
        {
            if (c == null) return 0;
            var amp = AreaMassProperties.Compute(c);
            return amp?.Area ?? 0;
        }
    }
}
