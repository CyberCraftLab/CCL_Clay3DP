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
using Rhino.Geometry.Intersect;

namespace CCL_Clay3DP.Core
{
    public static class ContourSlicer
    {
        /// <summary>
        /// Slice geometry with horizontal planes at the given layer height.
        /// Returns one closed curve per layer, sorted bottom to top.
        /// Attempts to cap open Breps before slicing.
        /// </summary>
        public static List<Curve> SliceBrep(Brep brep, double layerHeight,
            double offsetBottom, double offsetTop,
            Action<double> progress = null)
        {
            var bbox = brep.GetBoundingBox(true);
            if (!bbox.IsValid)
                throw new Exception("Cannot compute bounding box for geometry");

            double zMin = bbox.Min.Z + offsetBottom;
            double zMax = bbox.Max.Z - offsetTop;
            if (zMin >= zMax)
                throw new Exception("Height offsets leave no sliceable range");

            var contours = new List<Curve>();
            double totalHeight = zMax - zMin;

            // Start slightly above zMin to avoid degenerate tangent slices
            // at the exact ground plane (point/line instead of loop).
            // This small offset keeps the spiral starting "at the ground".
            double z = zMin + 0.01;
            while (z < zMax)
            {
                progress?.Invoke((z - zMin) / totalHeight * 0.03); // 0-3%

                var plane = new Plane(new Point3d(0, 0, z), Vector3d.ZAxis);
                var intersections = Intersection.BrepPlane(brep, plane, 0.001,
                    out Curve[] curves, out Point3d[] points);

                if (intersections && curves != null && curves.Length > 0)
                {
                    var joined = JoinAndPickLargest(curves);
                    if (joined != null)
                        contours.Add(joined);
                }

                z += layerHeight;
            }

            if (contours.Count < 2)
                throw new Exception(
                    $"Only {contours.Count} contour(s) found. Need at least 2. " +
                    "Check layer height and geometry.");

            return contours;
        }

        /// <summary>
        /// Slice a mesh with horizontal planes.
        /// </summary>
        public static List<Curve> SliceMesh(Mesh mesh, double layerHeight,
            double offsetBottom, double offsetTop,
            Action<double> progress = null)
        {
            var bbox = mesh.GetBoundingBox(true);
            if (!bbox.IsValid)
                throw new Exception("Cannot compute bounding box for mesh");

            double zMin = bbox.Min.Z + offsetBottom;
            double zMax = bbox.Max.Z - offsetTop;
            if (zMin >= zMax)
                throw new Exception("Height offsets leave no sliceable range");

            var contours = new List<Curve>();
            double totalHeight = zMax - zMin;
            // Start slightly above zMin to avoid degenerate tangent slices
            double z = zMin + 0.01;

            while (z < zMax)
            {
                progress?.Invoke((z - zMin) / totalHeight * 0.3);

                var plane = new Plane(new Point3d(0, 0, z), Vector3d.ZAxis);
                var polylines = Intersection.MeshPlane(mesh, plane);

                if (polylines != null && polylines.Length > 0)
                {
                    var curves = polylines
                        .Select(p => (Curve)new PolylineCurve(p))
                        .ToArray();
                    var joined = JoinAndPickLargest(curves);
                    if (joined != null)
                        contours.Add(joined);
                }

                z += layerHeight;
            }

            if (contours.Count < 2)
                throw new Exception(
                    $"Only {contours.Count} contour(s) found. Need at least 2. " +
                    "Check layer height and geometry.");

            return contours;
        }

        /// <summary>
        /// Join curve fragments and return the longest closed curve.
        /// If no closed curves found, try to close the longest open curve.
        /// </summary>
        private static Curve JoinAndPickLargest(Curve[] curves)
        {
            if (curves.Length == 1)
            {
                var c = curves[0];
                if (!c.IsClosed)
                    c = CloseCurve(c);
                return c;
            }

            var joined = Curve.JoinCurves(curves, 0.1);
            if (joined == null || joined.Length == 0)
                return null;

            // First pass: pick the longest closed curve
            Curve best = null;
            double bestLength = 0;

            foreach (var c in joined)
            {
                if (c.IsClosed)
                {
                    double len = c.GetLength();
                    if (len > bestLength)
                    {
                        bestLength = len;
                        best = c;
                    }
                }
            }

            if (best != null)
                return best;

            // Second pass: try closing open curves (sorted by length, longest first)
            var sorted = joined.OrderByDescending(c => c.GetLength());
            foreach (var c in sorted)
            {
                var closed = CloseCurve(c);
                if (closed != null)
                    return closed;
            }

            return null;
        }

        /// <summary>
        /// Close a nearly-closed curve by adding a line segment from end to start.
        /// Uses proportional gap tolerance based on curve length.
        /// </summary>
        private static Curve CloseCurve(Curve curve)
        {
            if (curve.IsClosed)
                return curve;

            double gap = curve.PointAtStart.DistanceTo(curve.PointAtEnd);
            double length = curve.GetLength();

            // Allow closing if gap is less than 20% of curve length
            // This handles tilted geometry where upper slices have larger openings
            double maxGap = Math.Max(5.0, length * 0.2);
            if (gap > maxGap)
                return null;

            var line = new LineCurve(curve.PointAtEnd, curve.PointAtStart);
            var joined = Curve.JoinCurves(new[] { curve, line }, 0.1);
            if (joined != null && joined.Length == 1 && joined[0].IsClosed)
                return joined[0];

            return null;
        }
    }
}
