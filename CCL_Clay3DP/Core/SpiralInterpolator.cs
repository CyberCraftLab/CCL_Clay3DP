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
    public static class SpiralInterpolator
    {
        /// <summary>
        /// Generate a continuous spiral toolpath by interpolating between
        /// consecutive contour curves. The Z coordinate ramps linearly from
        /// one contour's height to the next, creating a seamless spiral
        /// with no Z-jumps (vase mode).
        /// </summary>
        /// <param name="contours">Closed curves sorted bottom to top.</param>
        /// <param name="pointsPerLayer">Number of sample points per contour revolution.</param>
        /// <param name="startAngle">Start angle in degrees (0 = +X direction).</param>
        /// <param name="ccw">True for counter-clockwise, false for clockwise.</param>
        /// <returns>Ordered list of toolpath points forming a continuous spiral.</returns>
        public static List<Point3d> Interpolate(
            List<Curve> contours,
            int pointsPerLayer,
            double startAngle,
            bool ccw,
            Action<double> progress = null)
        {
            if (contours.Count < 2)
                throw new Exception("Need at least 2 contours to interpolate");

            // Ensure all contours have consistent direction
            AlignContourDirections(contours, ccw);

            // Align seam points so the spiral doesn't twist randomly
            AlignSeamPoints(contours, startAngle);

            var spiralPoints = new List<Point3d>();

            // Walk between consecutive contour pairs
            int totalLayers = contours.Count - 1;
            for (int layer = 0; layer < totalLayers; layer++)
            {
                progress?.Invoke(0.03 + (double)layer / totalLayers * 0.02); // 3-5%

                var lower = contours[layer];
                var upper = contours[layer + 1];

                double lowerZ = ContourZ(lower);
                double upperZ = ContourZ(upper);

                double lowerLength = lower.GetLength();
                double upperLength = upper.GetLength();

                for (int i = 0; i < pointsPerLayer; i++)
                {
                    // Normalized parameter along the contour [0, 1)
                    double t = (double)i / pointsPerLayer;

                    // Fraction through this layer for Z interpolation
                    double zFrac = t;
                    double z = lowerZ + (upperZ - lowerZ) * zFrac;

                    // Sample both contours at the same normalized parameter
                    Point3d ptLower = SampleAtNormalized(lower, t);
                    Point3d ptUpper = SampleAtNormalized(upper, t);

                    // Interpolate XY position between lower and upper contour
                    double x = ptLower.X + (ptUpper.X - ptLower.X) * zFrac;
                    double y = ptLower.Y + (ptUpper.Y - ptLower.Y) * zFrac;

                    spiralPoints.Add(new Point3d(x, y, z));
                }
            }

            // Add the final point at the top of the last contour
            var lastContour = contours[contours.Count - 1];
            spiralPoints.Add(SampleAtNormalized(lastContour, 0.0));

            return spiralPoints;
        }

        /// <summary>
        /// Create an interpolated curve through the spiral points.
        /// </summary>
        public static Curve CreateSpiralCurve(List<Point3d> points, int degree = 3)
        {
            if (points.Count < 2)
                return null;

            var curve = Curve.CreateInterpolatedCurve(points, degree);
            return curve;
        }

        /// <summary>
        /// Ensure all contours wind in the same direction.
        /// </summary>
        private static void AlignContourDirections(List<Curve> contours, bool ccw)
        {
            foreach (var contour in contours)
            {
                // CurveOrientation returns CounterClockwise or Clockwise
                // when viewed from above (looking down -Z)
                var orientation = contour.ClosedCurveOrientation(Vector3d.ZAxis);

                bool isCCW = orientation == CurveOrientation.CounterClockwise;
                if (isCCW != ccw)
                    contour.Reverse();
            }
        }

        /// <summary>
        /// Align seam points across contours so the spiral starts at a
        /// consistent angular position. Uses the start angle to define
        /// the seam direction from each contour's centroid.
        /// </summary>
        private static void AlignSeamPoints(List<Curve> contours, double startAngleDegrees)
        {
            double angleRad = startAngleDegrees * Math.PI / 180.0;
            var seamDir = new Vector3d(Math.Cos(angleRad), Math.Sin(angleRad), 0);

            foreach (var contour in contours)
            {
                // Find the centroid of the contour
                var areaProps = AreaMassProperties.Compute(contour);
                if (areaProps == null) continue;

                Point3d centroid = areaProps.Centroid;

                // Project a point outward from centroid in the seam direction
                Point3d seamTarget = centroid + seamDir * 10000;
                seamTarget = new Point3d(seamTarget.X, seamTarget.Y, centroid.Z);

                // Find closest point on contour to the seam target direction
                if (contour.ClosestPoint(seamTarget, out double t))
                    contour.ChangeClosedCurveSeam(t);
            }
        }

        /// <summary>
        /// Sample a closed curve at a normalized parameter [0, 1).
        /// Uses arc-length parameterization for even spacing.
        /// </summary>
        private static Point3d SampleAtNormalized(Curve curve, double normalizedT)
        {
            double length = curve.GetLength();
            double targetLength = normalizedT * length;

            if (!curve.LengthParameter(targetLength, out double param))
            {
                // Fallback: use domain-based parameter
                double domainT = curve.Domain.ParameterAt(normalizedT);
                return curve.PointAt(domainT);
            }

            return curve.PointAt(param);
        }

        /// <summary>
        /// Get the Z height of a contour (average Z of control points / sample points).
        /// </summary>
        private static double ContourZ(Curve contour)
        {
            // For a planar horizontal contour, any point's Z is the height
            return contour.PointAtStart.Z;
        }
    }
}
