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
