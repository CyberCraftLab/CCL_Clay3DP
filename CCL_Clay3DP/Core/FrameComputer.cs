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
    public static class FrameComputer
    {
        /// <summary>
        /// Compute oriented tool frames at each spiral point.
        /// Each frame has:
        ///   Origin = toolpath point
        ///   ZAxis  = surface normal at closest point (outward)
        ///   XAxis  = tangent along the toolpath
        ///   YAxis  = cross product completing the right-hand frame
        /// </summary>
        public static List<Plane> ComputeFrames(
            List<Point3d> spiralPoints,
            Brep brep,
            Mesh mesh,
            bool normalOutward,
            Action<double> progress = null)
        {
            var frames = new List<Plane>();
            int total = spiralPoints.Count;

            for (int i = 0; i < total; i++)
            {
                if (i % 50 == 0)
                    progress?.Invoke(0.05 + (double)i / total * 0.75); // 5-80%

                Point3d pt = spiralPoints[i];

                // 1) Get surface normal
                Vector3d normal = GetSurfaceNormal(pt, brep, mesh);
                if (!normal.IsValid || normal.IsZero)
                    continue;

                if (!normalOutward)
                    normal = -normal;

                // 2) Compute tangent from neighboring points
                Vector3d tangent = ComputeTangent(spiralPoints, i);
                if (!tangent.IsValid || tangent.IsZero)
                    continue;

                // 3) Build orthonormal frame
                //    Normal = ZAxis (surface normal direction)
                //    Tangent = approximately XAxis (travel direction)
                //    Ensure orthogonality by projecting tangent onto plane perpendicular to normal
                var frame = BuildOrthonormalFrame(pt, normal, tangent);
                if (frame.HasValue)
                    frames.Add(frame.Value);
            }

            return frames;
        }

        private static Vector3d GetSurfaceNormal(Point3d pt, Brep brep, Mesh mesh)
        {
            // Try Brep first
            if (brep != null)
            {
                if (brep.ClosestPoint(pt, out Point3d closestPt, out ComponentIndex ci,
                    out double s, out double t, 1000.0, out Vector3d normal))
                {
                    if (normal.Unitize())
                        return normal;
                }
            }

            // Try Mesh
            if (mesh != null)
            {
                var meshPt = mesh.ClosestMeshPoint(pt, 0.0);
                if (meshPt != null)
                {
                    Vector3d normal = mesh.NormalAt(meshPt);
                    if (normal.Unitize())
                        return normal;
                }
            }

            // Fallback: radial direction from Z axis
            var radial = new Vector3d(pt.X, pt.Y, 0);
            if (radial.Unitize())
                return radial;

            return Vector3d.XAxis;
        }

        private static Vector3d ComputeTangent(List<Point3d> points, int index)
        {
            Vector3d tangent;

            if (index == 0)
                tangent = points[1] - points[0];
            else if (index == points.Count - 1)
                tangent = points[points.Count - 1] - points[points.Count - 2];
            else
                tangent = points[index + 1] - points[index - 1];

            tangent.Unitize();
            return tangent;
        }

        private static Plane? BuildOrthonormalFrame(Point3d origin,
            Vector3d normal, Vector3d tangent)
        {
            // Project tangent onto the plane perpendicular to normal
            double dot = tangent * normal;
            Vector3d xAxis = tangent - dot * normal;
            if (!xAxis.Unitize())
                return null;

            Vector3d yAxis = Vector3d.CrossProduct(normal, xAxis);
            if (!yAxis.Unitize())
                return null;

            // Re-orthogonalize xAxis
            xAxis = Vector3d.CrossProduct(yAxis, normal);
            if (!xAxis.Unitize())
                return null;

            return new Plane(origin, xAxis, yAxis);
        }
    }
}
