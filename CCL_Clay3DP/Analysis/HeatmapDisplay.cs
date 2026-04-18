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
using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Analysis
{
    public static class HeatmapDisplay
    {
        private const string HeatmapLayerName = "3DP::Heatmap";

        /// <summary>
        /// Display clay feasibility directly on the geometry mesh.
        /// Each vertex is colored by its surface normal vs vertical (overhang).
        /// This is more accurate than frame-based analysis since it covers
        /// the entire surface, not just sampled spiral points.
        /// </summary>
        public static void ShowOnGeometry(
            RhinoDoc doc,
            Brep brep,
            Mesh inputMesh,
            ClayMaterialSettings clay)
        {
            ClearHeatmap(doc);

            // Get or create a render mesh from the geometry
            Mesh mesh;
            if (inputMesh != null)
            {
                mesh = inputMesh.DuplicateMesh();
            }
            else if (brep != null)
            {
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
                if (meshes == null || meshes.Length == 0)
                    return;
                mesh = new Mesh();
                foreach (var m in meshes)
                    mesh.Append(m);
            }
            else
            {
                return;
            }

            // Ensure normals are computed
            mesh.Normals.ComputeNormals();

            // Color each vertex by overhang angle
            mesh.VertexColors.Clear();
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                Vector3d normal = mesh.Normals[i];
                double score = ScoreVertexOverhang(normal, clay.MaxOverhangAngle);
                mesh.VertexColors.Add(ScoreToColor(score));
            }

            int layerIndex = EnsureHeatmapLayer(doc);
            var attrs = new ObjectAttributes
            {
                LayerIndex = layerIndex,
                Name = "Heatmap (Clay)",
                ColorSource = ObjectColorSource.ColorFromObject,
            };

            doc.Objects.AddMesh(mesh, attrs);
            doc.Views.Redraw();
        }

        /// <summary>
        /// Display robot dynamics analysis on the toolpath.
        /// Uses frame-based scoring since robot checks depend on the
        /// actual toolpath trajectory (angular velocity between frames).
        /// </summary>
        public static void ShowOnToolpath(
            RhinoDoc doc,
            List<Point3d> points,
            List<Plane> frames,
            RobotSettings robot,
            int framesPerLayer)
        {
            ClearHeatmap(doc);

            int count = Math.Min(points.Count, frames.Count);
            if (count < 2) return;

            var mesh = new Mesh();
            double ribbonHalf = 1.5;

            for (int i = 0; i < count; i++)
            {
                double score = ScoreWristVelocity(frames, points, i,
                    robot.FeedRate, robot.MaxWristAngularVelocity);
                Color color = ScoreToColor(score);

                Point3d pt = points[i];
                Vector3d tangent;
                if (i == 0)
                    tangent = points[1] - points[0];
                else if (i == count - 1)
                    tangent = points[count - 1] - points[count - 2];
                else
                    tangent = points[i + 1] - points[i - 1];
                tangent.Unitize();

                var perp = Vector3d.CrossProduct(tangent, Vector3d.ZAxis);
                if (!perp.Unitize())
                    perp = Vector3d.XAxis;

                mesh.Vertices.Add(pt - perp * ribbonHalf);
                mesh.Vertices.Add(pt + perp * ribbonHalf);
                mesh.VertexColors.Add(color);
                mesh.VertexColors.Add(color);

                if (i > 0)
                {
                    int v = mesh.Vertices.Count;
                    mesh.Faces.AddFace(v - 4, v - 3, v - 1, v - 2);
                }
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();

            int layerIndex = EnsureHeatmapLayer(doc);
            var attrs = new ObjectAttributes
            {
                LayerIndex = layerIndex,
                Name = "Heatmap (Robot)",
                ColorSource = ObjectColorSource.ColorFromObject,
            };

            doc.Objects.AddMesh(mesh, attrs);
            doc.Views.Redraw();
        }

        /// <summary>
        /// Display combined (worst-of-both) heatmap on the geometry mesh.
        /// Clay scores come from vertex normals, robot scores from nearest
        /// toolpath frame.
        /// </summary>
        public static void ShowCombined(
            RhinoDoc doc,
            Brep brep,
            Mesh inputMesh,
            List<Point3d> toolpathPoints,
            List<Plane> frames,
            ClayMaterialSettings clay,
            RobotSettings robot)
        {
            ClearHeatmap(doc);

            Mesh mesh;
            if (inputMesh != null)
                mesh = inputMesh.DuplicateMesh();
            else if (brep != null)
            {
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
                if (meshes == null || meshes.Length == 0) return;
                mesh = new Mesh();
                foreach (var m in meshes) mesh.Append(m);
            }
            else return;

            mesh.Normals.ComputeNormals();
            mesh.VertexColors.Clear();

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                Vector3d normal = mesh.Normals[i];
                double clayScore = ScoreVertexOverhang(normal, clay.MaxOverhangAngle);

                // Find nearest toolpath point for robot score
                double robotScore = 1.0;
                Point3d vertPt = mesh.Vertices[i];
                double minDist = double.MaxValue;
                int nearest = -1;
                // Sample every 10th point for speed
                for (int j = 0; j < toolpathPoints.Count; j += 10)
                {
                    double d = vertPt.DistanceToSquared(toolpathPoints[j]);
                    if (d < minDist)
                    {
                        minDist = d;
                        nearest = j;
                    }
                }
                if (nearest >= 0 && nearest < frames.Count)
                {
                    robotScore = ScoreWristVelocity(frames, toolpathPoints, nearest,
                        robot.FeedRate, robot.MaxWristAngularVelocity);
                }

                double combined = Math.Min(clayScore, robotScore);
                mesh.VertexColors.Add(ScoreToColor(combined));
            }

            int layerIndex = EnsureHeatmapLayer(doc);
            var attrs = new ObjectAttributes
            {
                LayerIndex = layerIndex,
                Name = "Heatmap (Combined)",
                ColorSource = ObjectColorSource.ColorFromObject,
            };

            doc.Objects.AddMesh(mesh, attrs);
            doc.Views.Redraw();
        }

        /// <summary>
        /// Score overhang from a mesh vertex normal.
        ///
        /// For clay printing, the key question is: how far is this surface
        /// from being vertical? A vertical wall can always be printed.
        /// Any deviation from vertical — whether tilting outward (normal
        /// points up) or inward (normal points down) — is overhang.
        ///
        /// Normal pointing horizontally (Z=0) → vertical wall → 0° overhang → safe
        /// Normal pointing up at 45° → surface tilts 45° from vertical → 45° overhang
        /// Normal pointing straight up → horizontal surface (ceiling/floor) → 90° overhang
        /// Normal pointing down → underside → 90°+ overhang → worst
        /// </summary>
        private static double ScoreVertexOverhang(Vector3d normal, double maxOverhangDeg)
        {
            if (!normal.Unitize())
                return 1.0;

            // Angle between normal and horizontal plane = how far from vertical the surface is.
            // |normal.Z| = cos(angle from vertical) = sin(angle from horizontal)
            // A perfectly vertical wall has normal.Z = 0 → overhang = 0°
            // A horizontal surface has |normal.Z| = 1 → overhang = 90°
            double overhangDeg = Math.Abs(Math.Asin(
                Math.Max(-1, Math.Min(1, normal.Z)))) * 180.0 / Math.PI;

            if (overhangDeg <= 0)
                return 1.0;
            if (overhangDeg >= maxOverhangDeg * 1.5)
                return 0.0;

            return Math.Max(0, 1.0 - overhangDeg / (maxOverhangDeg * 1.5));
        }

        private static double ScoreWristVelocity(List<Plane> frames,
            List<Point3d> points, int i, double feedRate, double maxAngVel)
        {
            if (i <= 0 || i >= frames.Count - 1)
                return 1.0;

            double angleZ = Vector3d.VectorAngle(frames[i - 1].ZAxis, frames[i].ZAxis);
            double angleX = Vector3d.VectorAngle(frames[i - 1].XAxis, frames[i].XAxis);
            double angle = Math.Max(angleZ, angleX);
            double angleDeg = angle * 180.0 / Math.PI;

            double dist = points[i].DistanceTo(points[i - 1]);
            if (dist < 1e-6) return 1.0;

            double time = dist / feedRate;
            double angularVelocity = angleDeg / time;

            if (angularVelocity <= maxAngVel * 0.5) return 1.0;
            if (angularVelocity >= maxAngVel) return 0.0;

            return 1.0 - (angularVelocity - maxAngVel * 0.5) / (maxAngVel * 0.5);
        }

        private static Color ScoreToColor(double score)
        {
            score = Math.Max(0, Math.Min(1, score));

            int r, g, b;
            if (score < 0.5)
            {
                double t = score / 0.5;
                r = 255;
                g = (int)(220 * t);
                b = 0;
            }
            else
            {
                double t = (score - 0.5) / 0.5;
                r = (int)(255 * (1 - t));
                g = (int)(180 + 20 * t);
                b = 0;
            }

            return Color.FromArgb(r, g, b);
        }

        public static void ClearHeatmap(RhinoDoc doc)
        {
            int layerIndex = FindNestedLayer(doc, HeatmapLayerName);
            if (layerIndex < 0) return;

            var objs = doc.Objects.FindByLayer(doc.Layers[layerIndex]);
            if (objs != null)
            {
                foreach (var obj in objs)
                    doc.Objects.Delete(obj, true);
            }
        }

        private static int EnsureHeatmapLayer(RhinoDoc doc)
        {
            string[] parts = HeatmapLayerName.Split(new[] { "::" }, StringSplitOptions.None);
            int parentIndex = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int found = -1;

                for (int li = 0; li < doc.Layers.Count; li++)
                {
                    var layer = doc.Layers[li];
                    if (layer.Name == part && layer.ParentLayerId ==
                        (parentIndex < 0 ? Guid.Empty : doc.Layers[parentIndex].Id))
                    {
                        found = li;
                        break;
                    }
                }

                if (found < 0)
                {
                    var newLayer = new Layer
                    {
                        Name = part,
                        Color = Color.FromArgb(255, 200, 0),
                    };
                    if (parentIndex >= 0)
                        newLayer.ParentLayerId = doc.Layers[parentIndex].Id;
                    found = doc.Layers.Add(newLayer);
                }

                parentIndex = found;
            }

            return parentIndex;
        }

        private static int FindNestedLayer(RhinoDoc doc, string fullPath)
        {
            string[] parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None);
            int parentIndex = -1;

            foreach (string part in parts)
            {
                int found = -1;
                for (int li = 0; li < doc.Layers.Count; li++)
                {
                    var layer = doc.Layers[li];
                    if (layer.Name == part && layer.ParentLayerId ==
                        (parentIndex < 0 ? Guid.Empty : doc.Layers[parentIndex].Id))
                    {
                        found = li;
                        break;
                    }
                }
                if (found < 0) return -1;
                parentIndex = found;
            }

            return parentIndex;
        }
    }
}
