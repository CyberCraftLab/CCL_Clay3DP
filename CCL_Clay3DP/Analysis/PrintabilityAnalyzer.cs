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
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Analysis
{
    public static class PrintabilityAnalyzer
    {
        /// <summary>
        /// Analyze the spiral toolpath for clay printability and robot dynamics.
        /// Returns a per-frame score (0 = fail, 1 = safe) for each check.
        /// </summary>
        public static PrintabilityResult Analyze(
            List<Point3d> points,
            List<Plane> frames,
            ClayMaterialSettings clay,
            RobotSettings robot,
            int framesPerLayer)
        {
            var result = new PrintabilityResult();
            int count = Math.Min(points.Count, frames.Count);

            for (int i = 0; i < count; i++)
            {
                var score = new FrameScore();

                // --- Clay checks ---

                // 1) Overhang angle: how far is the surface normal from vertical?
                score.OverhangScore = ScoreOverhang(frames[i], clay.MaxOverhangAngle);

                // 2) Layer bond: local layer height vs bead diameter
                score.BondScore = ScoreBond(points, i, framesPerLayer,
                    clay.BeadDiameter, clay.MinLayerBondRatio);

                // 3) Curvature: tight turns relative to bead diameter
                score.CurvatureScore = ScoreCurvature(points, i, clay.BeadDiameter);

                // 4) Taper rate: radial change between layers
                score.TaperScore = ScoreTaper(points, i, framesPerLayer,
                    clay.MaxOverhangAngle);

                // --- Robot checks ---

                // 5) Wrist angular velocity
                score.WristVelocityScore = ScoreWristVelocity(
                    frames, points, i, robot.FeedRate,
                    robot.MaxWristAngularVelocity);

                result.Scores.Add(score);
            }

            // Generate human-readable issues summary
            GenerateIssues(result, count);

            return result;
        }

        /// <summary>
        /// Score the overhang angle at this frame.
        /// 0 degrees from vertical = perfectly safe (score 1.0)
        /// At max overhang = marginal (score 0.5)
        /// Beyond max overhang = fail (score approaching 0)
        /// </summary>
        private static double ScoreOverhang(Plane frame, double maxOverhangDeg)
        {
            // The frame's ZAxis is the surface normal (outward).
            // Overhang angle = angle between normal and vertical, minus 90.
            // For a vertical wall, normal is horizontal → angle to Z = 90 → overhang = 0.
            // For a ceiling, normal points down → angle to Z = 180 → overhang = 90.
            Vector3d normal = frame.ZAxis;
            double angleToVertical = Vector3d.VectorAngle(normal, Vector3d.ZAxis);
            double angleToVerticalDeg = angleToVertical * 180.0 / Math.PI;

            // Overhang from vertical: 0 = vertical wall, 90 = horizontal ceiling
            // A vertical wall has normal pointing horizontally → angleToVertical = 90
            // Overhang = angleToVertical - 90 (for outward-facing normals on a wall)
            // But for the top of a dome, normal points up → angleToVertical = 0 → no overhang
            // For an inward lean, normal tilts inward → overhang > 0

            // Simpler: the relevant angle is how far the surface deviates from vertical.
            // This is (90 - angle between normal and horizontal plane).
            // Or equivalently: angle between the surface tangent plane and vertical.
            // For clay: what matters is the angle of the wall from vertical.
            double overhangDeg = Math.Max(0, angleToVerticalDeg - 90.0);

            if (overhangDeg <= 0)
                return 1.0;
            if (overhangDeg >= maxOverhangDeg * 1.5)
                return 0.0;

            // Linear ramp: 1.0 at 0°, 0.5 at max, 0.0 at 1.5x max
            return Math.Max(0, 1.0 - overhangDeg / (maxOverhangDeg * 1.5));
        }

        /// <summary>
        /// Score layer-to-layer adhesion.
        /// Compares the vertical distance to the point one revolution ago.
        /// </summary>
        private static double ScoreBond(List<Point3d> points, int i,
            int framesPerLayer, double beadDiameter, double minRatio)
        {
            // Look back one full revolution
            int prev = i - framesPerLayer;
            if (prev < 0)
                return 1.0; // First layer, no bond check

            double dz = Math.Abs(points[i].Z - points[prev].Z);
            double ratio = dz / beadDiameter;

            if (ratio <= 1.0)
                return 1.0; // Layer height <= bead diameter, good adhesion
            if (ratio >= 1.0 / minRatio)
                return 0.0; // Too far apart

            // Linear interpolation
            return Math.Max(0, 1.0 - (ratio - 1.0) / (1.0 / minRatio - 1.0));
        }

        /// <summary>
        /// Score curvature — tight turns can cause nozzle drag.
        /// </summary>
        private static double ScoreCurvature(List<Point3d> points, int i,
            double beadDiameter)
        {
            if (i <= 0 || i >= points.Count - 1)
                return 1.0;

            // Approximate radius of curvature from 3 consecutive points
            Vector3d v1 = points[i] - points[i - 1];
            Vector3d v2 = points[i + 1] - points[i];

            double len1 = v1.Length;
            double len2 = v2.Length;
            if (len1 < 1e-6 || len2 < 1e-6)
                return 1.0;

            v1.Unitize();
            v2.Unitize();

            double cross = Vector3d.CrossProduct(v1, v2).Length;
            if (cross < 1e-10)
                return 1.0; // Straight line

            // Radius of curvature ≈ chord / (2 * sin(angle/2))
            double angle = Math.Asin(Math.Min(1.0, cross));
            double chord = (len1 + len2) * 0.5;
            double radius = chord / (2.0 * Math.Sin(angle * 0.5 + 1e-10));

            double minRadius = beadDiameter * 2.0;
            if (radius >= minRadius * 2.0)
                return 1.0;
            if (radius <= minRadius * 0.5)
                return 0.0;

            return (radius - minRadius * 0.5) / (minRadius * 1.5);
        }

        /// <summary>
        /// Score taper rate — how quickly the radius changes between layers.
        /// </summary>
        private static double ScoreTaper(List<Point3d> points, int i,
            int framesPerLayer, double maxOverhangDeg)
        {
            int prev = i - framesPerLayer;
            if (prev < 0)
                return 1.0;

            // Radial distance from Z axis (approximate)
            double r1 = Math.Sqrt(points[prev].X * points[prev].X +
                                   points[prev].Y * points[prev].Y);
            double r2 = Math.Sqrt(points[i].X * points[i].X +
                                   points[i].Y * points[i].Y);
            double dz = Math.Abs(points[i].Z - points[prev].Z);

            if (dz < 1e-6)
                return 1.0;

            double dr = Math.Abs(r2 - r1);
            double taperAngleDeg = Math.Atan2(dr, dz) * 180.0 / Math.PI;

            if (taperAngleDeg <= maxOverhangDeg)
                return 1.0;
            if (taperAngleDeg >= maxOverhangDeg * 2.0)
                return 0.0;

            return 1.0 - (taperAngleDeg - maxOverhangDeg) / maxOverhangDeg;
        }

        /// <summary>
        /// Score wrist angular velocity — rate of tool orientation change.
        /// </summary>
        private static double ScoreWristVelocity(List<Plane> frames,
            List<Point3d> points, int i, double feedRate, double maxAngVel)
        {
            if (i <= 0 || i >= frames.Count - 1)
                return 1.0;

            // Angular change between consecutive frames
            double angle = AngleBetweenFrames(frames[i - 1], frames[i]);
            double angleDeg = angle * 180.0 / Math.PI;

            // Time to traverse between points at feed rate
            double dist = points[i].DistanceTo(points[i - 1]);
            if (dist < 1e-6)
                return 1.0;

            double time = dist / feedRate; // seconds
            double angularVelocity = angleDeg / time; // deg/s

            if (angularVelocity <= maxAngVel * 0.5)
                return 1.0;
            if (angularVelocity >= maxAngVel)
                return 0.0;

            return 1.0 - (angularVelocity - maxAngVel * 0.5) / (maxAngVel * 0.5);
        }

        /// <summary>
        /// Compute the angle between two frames (max axis rotation).
        /// </summary>
        private static double AngleBetweenFrames(Plane a, Plane b)
        {
            // Use the angle between Z axes as the primary rotation measure
            double angleZ = Vector3d.VectorAngle(a.ZAxis, b.ZAxis);
            double angleX = Vector3d.VectorAngle(a.XAxis, b.XAxis);
            return Math.Max(angleZ, angleX);
        }

        private static void GenerateIssues(PrintabilityResult result, int count)
        {
            int overhangFails = 0, bondFails = 0, curveFails = 0;
            int taperFails = 0, wristFails = 0;

            foreach (var s in result.Scores)
            {
                if (s.OverhangScore < 0.3) overhangFails++;
                if (s.BondScore < 0.3) bondFails++;
                if (s.CurvatureScore < 0.3) curveFails++;
                if (s.TaperScore < 0.3) taperFails++;
                if (s.WristVelocityScore < 0.3) wristFails++;
            }

            if (overhangFails > 0)
                result.Issues.Add($"Overhang: {overhangFails} frames exceed max angle");
            if (bondFails > 0)
                result.Issues.Add($"Bond: {bondFails} frames have weak layer adhesion");
            if (curveFails > 0)
                result.Issues.Add($"Curvature: {curveFails} frames have tight turns");
            if (taperFails > 0)
                result.Issues.Add($"Taper: {taperFails} frames have rapid diameter change");
            if (wristFails > 0)
                result.Issues.Add($"Wrist: {wristFails} frames exceed angular velocity limit");
        }
    }
}
