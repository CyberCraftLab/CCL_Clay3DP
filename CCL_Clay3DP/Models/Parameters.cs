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

        // Layer-slice only — ignored when SpiralSlice is true. Generates a
        // zigzag bracing pattern attached to the outer wall, anchored to a
        // virtual inner offset (computed but neither baked nor printed).
        // FramesPerLayer also controls the zigzag point count in this mode.
        public bool OuterWallBracing { get; set; } = false;

        // Spiral-slice only — ignored when SpiralSlice is false. When true the
        // build plate tilts so the tool follows the spiral curve like an
        // airplane: fuselage along the curve tangent T, wings along the
        // underlying Brep/Mesh surface normal N, and the "tail" / tool Z axis
        // along N × T. For vase-mode prints this keeps the plate nearly flat
        // with a slight pitch matching the spiral rise; for sloped / curved
        // geometry the plate banks with the surface. Off by default — current
        // Cartesian-Z behavior preserved for existing workflows.
        public bool SpiralFollowsCurveNormal { get; set; } = false;
    }

}
