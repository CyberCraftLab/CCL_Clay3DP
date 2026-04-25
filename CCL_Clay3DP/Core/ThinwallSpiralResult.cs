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
        public List<Curve> Contours { get; set; } = new List<Curve>();
        public Curve AxisCurve { get; set; }
        public double TotalHeight { get; set; }
        public int LayerCount { get; set; }

        // Skirt loop (Issue #8). The robot prints the skirt first, then
        // the part. Frames always use +Z world normal regardless of
        // SpiralFollowsCurveNormal — the skirt sits flat on the build
        // plate. Empty list = no skirt (e.g., slice failed to produce
        // a usable lowest contour).
        public List<Plane> SkirtFrames { get; set; } = new List<Plane>();
        public Curve SkirtCurve { get; set; }

        // Base layers (Issue #10). When BaseSettings.EnableBase, the
        // robot prints N base layers between the skirt and the part:
        //   skirt (z=0) → [contour + infill] × N → part (shifted up by N·h)
        // BaseFrames is the concatenated frame stream covering all base
        // layers, in print order. BaseContourCurves and InfillCurves are
        // bake-only visualizations (one entry per base layer). All base
        // frames use +Z world normal — the base is always horizontal.
        // Empty when base disabled or when the lowest contour was not
        // recoverable.
        public List<Plane> BaseFrames { get; set; } = new List<Plane>();
        public List<Curve> BaseContourCurves { get; set; } = new List<Curve>();
        public List<Curve> InfillCurves { get; set; } = new List<Curve>();
    }
}
