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
using Rhino.Geometry;

namespace CCL_Clay3DP.Core
{
    public class SpiralResult
    {
        public List<Point3d> ToolpathPoints { get; set; } = new List<Point3d>();
        public List<Plane> Frames { get; set; } = new List<Plane>();
        public Curve SpiralCurve { get; set; }
        public Mesh RibbonMesh { get; set; }
        public List<Curve> Contours { get; set; } = new List<Curve>();
        public Curve AxisCurve { get; set; }
        public double TotalHeight { get; set; }
        public int LayerCount { get; set; }
    }
}
