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
    /// Generates straight-line infill polylines clipped to a closed planar
    /// contour. Used by BaseBuilder (Issue #10) to fill the inside of each
    /// base layer with a 45-degree cross-hatch pattern, alternating ±45
    /// per layer for stability when stacked.
    ///
    /// Algorithm: rotate the contour so the desired infill angle aligns
    /// with the X axis, scan horizontally at the configured line spacing,
    /// intersect each scan line with the rotated contour, pair the hits
    /// into inside-only segments under the even-odd rule. Snake-order
    /// the segments and stitch them into ONE polyline as long as each
    /// straight connector between consecutive segments stays inside the
    /// contour. When a connector would cut across a concavity (e.g.,
    /// between the head and body of a non-convex outline) the polyline
    /// breaks and a new one starts at the next segment — this prevents
    /// the "leak" of infill material into the void outside the contour.
    /// Returns a list of polylines per layer; usually 1 for convex shapes,
    /// many for highly non-convex ones.
    /// </summary>
    public static class InfillGenerator
    {
        /// <summary>
        /// Build snake-pattern infill polylines for one base layer.
        /// </summary>
        /// <param name="contour">Closed planar curve. Must lie at z=0
        /// for the rotation math; results are translated to <paramref
        /// name="z"/> after generation.</param>
        /// <param name="z">Target world Z for the resulting polylines.</param>
        /// <param name="angleDegrees">Infill direction in degrees,
        /// measured CCW from +X. Use +45 for even layers, -45 for odd.</param>
        /// <param name="lineSpacing">Distance between adjacent infill
        /// scan lines, in mm. Slice 2+3 default = 1.5 × BeadDiameter.</param>
        /// <returns>One or more polyline curves covering the inside of
        /// the contour. Empty list if the contour is unusable or no scan
        /// line produced a segment.</returns>
        public static List<Curve> GenerateCrossHatch(
            Curve contour, double z, double angleDegrees, double lineSpacing)
        {
            var output = new List<Curve>();
            if (contour == null || !contour.IsClosed) return output;
            if (lineSpacing <= 0.0) return output;

            double angleRad = angleDegrees * Math.PI / 180.0;

            // Rotate contour by -angle so the chosen infill direction
            // aligns with +X — that lets us scan in horizontal lines and
            // unrotate at the end.
            var rot = Transform.Rotation(-angleRad, Vector3d.ZAxis, Point3d.Origin);
            var unrot = Transform.Rotation(angleRad, Vector3d.ZAxis, Point3d.Origin);

            var rotatedContour = contour.DuplicateCurve();
            rotatedContour.Transform(rot);

            var bbox = rotatedContour.GetBoundingBox(true);
            if (!bbox.IsValid) return output;

            // Pad scan-line endpoints beyond the bbox so we get clean
            // intersections even when the contour just touches a side.
            double xMin = bbox.Min.X - 1.0;
            double xMax = bbox.Max.X + 1.0;

            // Inset the first scan line by half the spacing so the
            // pattern is centered in the contour's Y extent — avoids
            // a near-zero-length sliver at the bottom.
            double yLo = bbox.Min.Y + lineSpacing * 0.5;
            double yHi = bbox.Max.Y;

            // Each scan line yields zero or more (entry, exit) point pairs;
            // we collect them as oriented segments in print order.
            var segments = new List<(Point3d a, Point3d b)>();

            // Snake direction: alternate per scan line so consecutive
            // lines connect at adjacent endpoints rather than diagonally
            // across the contour.
            bool flip = false;

            for (double y = yLo; y < yHi; y += lineSpacing)
            {
                var scan = new Line(
                    new Point3d(xMin, y, 0.0),
                    new Point3d(xMax, y, 0.0));
                var ix = Intersection.CurveLine(
                    rotatedContour, scan, 0.001, 0.001);
                if (ix == null || ix.Count < 2) continue;

                var hits = new List<Point3d>();
                foreach (var ev in ix)
                {
                    if (ev.IsPoint) hits.Add(ev.PointA);
                }
                if (hits.Count < 2) continue;

                // Sort by X. Pair consecutive hits as in/out spans
                // (even-odd rule). For non-convex contours we may get >2
                // hits per scan; pairing (0,1),(2,3),… captures the
                // inside spans correctly.
                hits.Sort((p, q) => p.X.CompareTo(q.X));

                // Build the row's segments in scan-direction order, then
                // reverse the row when flip is true so adjacent rows
                // connect at the same edge.
                var rowSegments = new List<(Point3d a, Point3d b)>();
                for (int i = 0; i + 1 < hits.Count; i += 2)
                    rowSegments.Add((hits[i], hits[i + 1]));

                if (flip)
                {
                    rowSegments.Reverse();
                    for (int i = 0; i < rowSegments.Count; i++)
                    {
                        var s = rowSegments[i];
                        rowSegments[i] = (s.b, s.a);
                    }
                }

                segments.AddRange(rowSegments);
                flip = !flip;
            }

            if (segments.Count == 0) return output;

            // Stitch segments into polylines, breaking whenever the
            // straight connector to the next segment would leave the
            // contour. The break is the v1 fix for the "leak" bug
            // observed on non-convex contours: rather than draw a line
            // across a concavity, we end the current polyline and start
            // a fresh one at the next segment. Travel between disjoint
            // polylines happens at op-speed during print (same as the
            // skirt-to-part transition); future work can lift the
            // extruder for cleaner travels.
            var plane = new Plane(new Point3d(0.0, 0.0, 0.0), Vector3d.ZAxis);
            var current = new List<Point3d> { segments[0].a, segments[0].b };

            for (int i = 1; i < segments.Count; i++)
            {
                Point3d prev = current[current.Count - 1];
                Point3d a = segments[i].a;
                Point3d b = segments[i].b;

                if (ConnectorStaysInside(prev, a, rotatedContour, plane))
                {
                    current.Add(a);
                    current.Add(b);
                }
                else
                {
                    EmitPolyline(current, unrot, z, output);
                    current = new List<Point3d> { a, b };
                }
            }
            EmitPolyline(current, unrot, z, output);

            return output;
        }

        /// <summary>
        /// Sample the straight line a→b at a handful of interior points
        /// and check that all of them lie inside (or on) the contour.
        /// Five samples is enough to catch the connector-across-concavity
        /// case without paying for a full intersection test on every
        /// candidate hop. Endpoints themselves are not tested — they
        /// already lie on the contour by construction (they're scan-line
        /// intersection points).
        /// </summary>
        private static bool ConnectorStaysInside(
            Point3d a, Point3d b, Curve contour, Plane plane)
        {
            const int samples = 5;
            var dir = b - a;
            for (int i = 1; i < samples; i++)
            {
                double t = i / (double)samples;
                var p = a + t * dir;
                var c = contour.Contains(p, plane, 0.001);
                if (c != PointContainment.Inside && c != PointContainment.Coincident)
                    return false;
            }
            return true;
        }

        private static void EmitPolyline(
            List<Point3d> pts, Transform unrot, double z, List<Curve> output)
        {
            if (pts == null || pts.Count < 2) return;
            var pl = new Polyline(pts);
            pl.Transform(unrot);
            if (Math.Abs(z) > 1e-9)
                pl.Transform(Transform.Translation(0.0, 0.0, z));
            output.Add(new PolylineCurve(pl));
        }
    }
}
