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

using Rhino.Geometry;

namespace CCL_Clay3DP.Models
{
    public enum SliceMode
    {
        Vase,
        Tilted,
        Freeform,
    }

    public class GeometrySelection
    {
        public Brep Brep { get; set; }
        public Mesh Mesh { get; set; }
    }

    public class HeightParameters
    {
        public double HeightOffsetBottom { get; set; } = 0.0;
        public double HeightOffsetTop { get; set; } = 0.0;
    }

    public class HelixParameters
    {
        public double LayerHeight { get; set; } = 4.0;
        public double RadialOffset { get; set; } = 0.0;
        public double StartAngle { get; set; } = 0.0;
        public bool DirectionCCW { get; set; } = true;
        public int FramesPerLayer { get; set; } = 360;

        // Toolpath mode. When true, the Slice button produces a continuous
        // spiral (original behavior). When false, it produces discrete
        // planar layer contours; inner-wall bracing (below) then governs
        // whether we also emit the inner curve + zigzag structural pattern.
        public bool SpiralSlice { get; set; } = true;

        // Layer-slice only — ignored when SpiralSlice is true. Generates the
        // inner wall curves and zigzag pattern between outer and inner as a
        // structural bracing. FramesPerLayer also controls the zigzag point
        // count in this mode.
        public bool InnerWallBracing { get; set; } = false;
    }

}
