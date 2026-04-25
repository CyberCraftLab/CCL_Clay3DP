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

namespace CCL_Clay3DP.Models
{
    public class RobotSettings
    {
        public double FeedRate { get; set; } = 100.0;
        public double TravelSpeed { get; set; } = 166.7;

        public double MaxWristAngularVelocity { get; set; } = 90.0;
        public double SpindleSpeed { get; set; } = 500.0;
        public string NozzleTool { get; set; } = "T10";

        public string RoboDKExecutablePath { get; set; } = @"C:\RoboDK\bin\RoboDK.exe";
        public string RoboDKStationTemplatePath { get; set; } = @"C:\Users\Thinkpad\Documents\3DP\robodk_station\3DP_v0.4.rdk";
        public string RoboDKProjectName { get; set; } = "3DP";
        public string RoboDKPythonExe { get; set; } = @"C:\RoboDK\Python-Embedded\python.exe";
        public string RoboDKApiPath { get; set; } = @"C:\RoboDK\Python";
    }
}
