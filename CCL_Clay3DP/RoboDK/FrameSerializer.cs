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
        /// Normals point along Z-axis (nozzle is fixed vertical,
        /// robot holds the build plate).
        /// </summary>
        public static string SerializeToFile(List<Plane> frames, RobotSettings robot,
            double layerHeight)
        {
            var points = new List<double[]>();
            foreach (var frame in frames)
            {
                points.Add(new[]
                {
                    frame.Origin.X, frame.Origin.Y, frame.Origin.Z,
                    0.0, 0.0, 1.0,
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
