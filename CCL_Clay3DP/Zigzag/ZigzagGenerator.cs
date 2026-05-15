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

namespace CCL_Clay3DP.Zigzag
{
    public class SimpleZigzagResult
    {
        public List<Point3d> OuterPoints { get; set; } = new List<Point3d>();
        public List<Point3d> InnerPoints { get; set; } = new List<Point3d>();
        public Curve InnerCurve { get; set; }
        public Curve Zigzag { get; set; }
        public bool IsClosed { get; set; }
    }

    /// <summary>
    /// Single-contour zigzag. Divides the contour into N equally-spaced points,
    /// projects each inward along the curve's in-plane normal by a chosen
    /// distance, then weaves a triangle-wave polyline between outer and inner.
    ///
    /// A contour is treated as closed when its start and end points coincide
    /// (IsClosed==true or endpoints within tolerance). Open contours yield
    /// open inner/zigzag polylines running end to end.
    /// </summary>
    public static class ZigzagGenerator
    {
        // Tolerance for "start==end" detection on curves that aren't flagged closed
        private const double CloseEndpointTol = 0.001;

        public static SimpleZigzagResult BuildSingleContour(
            Curve contour, int numPoints, double inwardDistance,
            bool flipInward = false, double wallOffset = 0.0)
        {
            if (contour == null) throw new Exception("Contour is null");
            if (inwardDistance <= 0) throw new Exception("Inward distance must be positive");
            if (wallOffset < 0) throw new Exception("Wall offset must be non-negative");
            if (numPoints < 4) throw new Exception("Need at least 4 points");
            if (numPoints % 2 != 0) numPoints++; // even N closes the zigzag cleanly

            // Treat as closed when start==end, even if IsClosed is false.
            bool isClosed = contour.IsClosed
                || contour.PointAtStart.DistanceTo(contour.PointAtEnd) < CloseEndpointTol;

            // Make the curve actually closed so orientation/seam ops work.
            if (isClosed && !contour.IsClosed)
            {
                if (!contour.MakeClosed(CloseEndpointTol))
                    isClosed = false;
            }

            // For closed: detect winding + align seam to +X so points stack
            // across layers. For open: skip both — divide end-to-end.
            bool isCCW = true;
            if (isClosed)
            {
                var orient = contour.ClosedCurveOrientation(Vector3d.ZAxis);
                isCCW = orient != CurveOrientation.Clockwise;

                var areaProps = AreaMassProperties.Compute(contour);
                if (areaProps != null)
                {
                    var c = areaProps.Centroid;
                    var seamTarget = new Point3d(c.X + 10000, c.Y, c.Z);
                    if (contour.ClosestPoint(seamTarget, out double seamT))
                        contour.ChangeClosedCurveSeam(seamT);
                }
            }

            // Equal-length division.
            //   Closed curve: returns N params.
            //   Open curve  : returns N+1 params (includes both endpoints).
            var ts = contour.DivideByCount(numPoints, true);
            if (ts == null || ts.Length < numPoints)
                throw new Exception("Could not divide contour into equal parts");

            int count = ts.Length;
            var outer = new List<Point3d>(count);
            var inner = new List<Point3d>(count);

            for (int i = 0; i < count; i++)
            {
                double t = ts[i];
                Point3d p = contour.PointAt(t);
                Vector3d tan = contour.TangentAt(t);
                tan.Z = 0;
                if (!tan.Unitize())
                {
                    outer.Add(p);
                    inner.Add(p);
                    continue;
                }

                // Closed CCW (and open default): inward = 90° CCW rotation = (-ty, tx).
                // Closed CW:                     inward = 90° CW rotation  = ( ty,-tx).
                Vector3d inwardDir = isCCW
                    ? new Vector3d(-tan.Y, tan.X, 0)
                    : new Vector3d(tan.Y, -tan.X, 0);
                if (flipInward) inwardDir = -inwardDir;

                // wallOffset shifts the bracing's wall-contact point inward
                // from the contour centerline so the bracing bead's outer
                // edge tangentially meets the outer-wall bead at the
                // contour centerline ("french kiss" overlap — Issue #11
                // slice B). Inner anchor follows by the same offset, so
                // the user-facing inwardDistance is the tooth depth measured
                // from the kiss point — not from the contour.
                outer.Add(p + inwardDir * wallOffset);
                inner.Add(p + inwardDir * (wallOffset + inwardDistance));
            }

            // Zigzag alternation. For closed curves append start to close the
            // loop; for open curves leave it running end-to-end.
            var zz = new List<Point3d>(count + 1);
            for (int i = 0; i < count; i++)
                zz.Add(i % 2 == 0 ? outer[i] : inner[i]);
            if (isClosed) zz.Add(zz[0]);

            // Inner polyline through all inner points. Close only if outer was closed.
            var innerLoop = new List<Point3d>(inner);
            if (isClosed) innerLoop.Add(inner[0]);

            // Remove hairpin folds where the polyline walks into a needle-shaped
            // excursion and comes back out near the same spot. Detected by the
            // shortcut ratio |AC| / (|AB| + |BC|): ~1 for a straight line,
            // ~0.25-0.9 for a normal zigzag peak (depending on d/spacing), and
            // near 0 when A and C collapse together. Threshold 0.15 sits in the
            // gap so normal peaks survive. Outer contour is untouched.
            const double hairpinShortcutRatio = 0.15;
            zz = RemoveHairpins(zz, hairpinShortcutRatio, isClosed);
            innerLoop = RemoveHairpins(innerLoop, hairpinShortcutRatio, isClosed);

            return new SimpleZigzagResult
            {
                OuterPoints = outer,
                InnerPoints = inner,
                InnerCurve = new PolylineCurve(innerLoop),
                Zigzag = new PolylineCurve(zz),
                IsClosed = isClosed,
            };
        }

        /// <summary>
        /// Remove polyline vertices sitting in a hairpin fold, detected by a
        /// small shortcut ratio |AC|/(|AB|+|BC|). Iterates until no more
        /// qualifying vertices remain. For closed polylines the first/last
        /// duplicate closing vertex is preserved.
        /// </summary>
        private static List<Point3d> RemoveHairpins(
            List<Point3d> pts, double maxShortcutRatio, bool isClosed)
        {
            if (pts == null || pts.Count < 3) return pts;

            var result = new List<Point3d>(pts);
            bool hadClosureDup = isClosed
                && result.Count >= 2
                && result[0].DistanceTo(result[result.Count - 1]) < 1e-9;
            if (hadClosureDup) result.RemoveAt(result.Count - 1);

            int safety = 0;
            bool changed = true;
            while (changed && result.Count >= 3 && safety++ < 1000)
            {
                changed = false;
                for (int i = 0; i < result.Count; i++)
                {
                    // Open polylines: skip endpoints (no predecessor/successor).
                    if (!isClosed && (i == 0 || i == result.Count - 1)) continue;

                    int prev = (i - 1 + result.Count) % result.Count;
                    int next = (i + 1) % result.Count;

                    double dAB = result[prev].DistanceTo(result[i]);
                    double dBC = result[i].DistanceTo(result[next]);
                    double dAC = result[prev].DistanceTo(result[next]);
                    double edgeSum = dAB + dBC;
                    if (edgeSum < 1e-9) continue;

                    double ratio = dAC / edgeSum;
                    if (ratio < maxShortcutRatio)
                    {
                        result.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            if (hadClosureDup && result.Count > 0)
                result.Add(result[0]);

            return result;
        }
    }
}
