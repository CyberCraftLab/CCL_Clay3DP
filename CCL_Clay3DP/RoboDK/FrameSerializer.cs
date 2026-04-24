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

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Rhino.Geometry;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.RoboDK
{
    public static class FrameSerializer
    {
        /// <summary>
        /// Serialize frames as 6xN curve data (x,y,z,i,j,k per point)
        /// plus all robot settings that need to be propagated to RoboDK.
        ///
        /// The per-point normal (i,j,k) is the vector RoboDK aligns the tool
        /// Z-axis to at each target. With the nozzle fixed vertical and the
        /// build plate held by the robot, that vector drives how the plate
        /// is oriented at each spiral point.
        ///
        /// <paramref name="followCurveNormal"/> controls which direction is
        /// written:
        ///   false → (0,0,1) world Z, build plate stays flat (default).
        ///   true  → frame.YAxis = N × T, the "airplane tail" direction of
        ///           the Darboux frame. Plate banks with the surface while
        ///           staying mostly vertical for vase-mode spirals.
        /// </summary>
        public static string SerializeToFile(List<Plane> frames, RobotSettings robot,
            double layerHeight, bool followCurveNormal)
        {
            var points = new List<double[]>();
            foreach (var frame in frames)
            {
                double nx, ny, nz;
                if (followCurveNormal)
                {
                    nx = frame.YAxis.X;
                    ny = frame.YAxis.Y;
                    nz = frame.YAxis.Z;
                }
                else
                {
                    nx = 0.0; ny = 0.0; nz = 1.0;
                }
                points.Add(new[]
                {
                    frame.Origin.X, frame.Origin.Y, frame.Origin.Z,
                    nx, ny, nz,
                });
            }

            var payload = new Dictionary<string, object>
            {
                ["feed_rate"] = robot.FeedRate,          // mm/s for operation speed
                ["travel_speed"] = robot.TravelSpeed,    // mm/s for approach/retract
                ["spindle_speed"] = robot.SpindleSpeed,  // S value
                ["nozzle_tool"] = robot.NozzleTool,      // T10 / T11 / T12
                ["layer_height"] = layerHeight,          // mm, for template name only
                ["point_count"] = frames.Count,
                ["points"] = points,
            };

            string tempPath = Path.Combine(Path.GetTempPath(), "auto3d_frames.json");
            string json = JsonConvert.SerializeObject(payload, Formatting.None);
            File.WriteAllText(tempPath, json);

            return tempPath;
        }
    }
}
