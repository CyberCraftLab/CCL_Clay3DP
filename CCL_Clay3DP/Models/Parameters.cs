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
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        // ObjectId of the source Rhino object the user picked, when one
        // exists. Empty Guid for synthetic / transformed copies (e.g.,
        // the result of ApplyTransform after Issue #1's auto-translation
        // — that copy has no document representation). Used by the
        // panel to hide the original on slice and restore it on settings
        // change.
        public System.Guid SourceObjectId { get; set; } = System.Guid.Empty;
    }

    public class HeightParameters
    {
        public double HeightOffsetBottom { get; set; } = 0.0;
        public double HeightOffsetTop { get; set; } = 0.0;
    }

    // Cell-specific build volume — the printable space the robot can
    // physically reach with the extruder. Used to render a wireframe
    // box at slice time so users can see whether their part fits, and
    // to center new geometry on the build plate.
    //
    // Slice 3 model: sized as Width × Depth × Height, centered on the
    // world origin in XY (so the build plate's center is at world 0,0,0
    // and matches the auto-translate target). Z always starts at 0 (the
    // build plate). XMin/XMax/YMin/YMax are exposed as JsonIgnore'd
    // computed properties so existing pipeline code (BakeBuildVolume,
    // BuildVolumeCheck) keeps working unchanged.
    public class BuildVolumeSettings
    {
        public double Width  { get; set; } = 400.0;
        public double Depth  { get; set; } = 400.0;
        public double Height { get; set; } = 1000.0;

        [JsonIgnore] public double XMin => -Width / 2.0;
        [JsonIgnore] public double XMax =>  Width / 2.0;
        [JsonIgnore] public double YMin => -Depth / 2.0;
        [JsonIgnore] public double YMax =>  Depth / 2.0;

        // Slice 3 — JSON migration from the pre-Slice-3 schema, which
        // wrote XMin/XMax/YMin/YMax instead of Width/Depth. Newtonsoft
        // captures unknown JSON fields here, then OnDeserialized rolls
        // them up into Width/Depth so the user's saved build volume
        // doesn't reset to defaults after the upgrade. Future saves
        // emit only Width/Depth/Height; the legacy fields disappear
        // from settings.json on next save.
        // CS0649 suppressed because Newtonsoft assigns this field via
        // reflection during deserialization — the compiler can't see the
        // assignment and warns "never assigned".
#pragma warning disable CS0649
        [JsonExtensionData]
        private IDictionary<string, JToken> _legacy;
#pragma warning restore CS0649

        [OnDeserialized]
        internal void OnDeserialized(StreamingContext context)
        {
            if (_legacy == null) return;
            if (_legacy.TryGetValue("XMin", out var xmin)
                && _legacy.TryGetValue("XMax", out var xmax))
            {
                Width = (double)xmax - (double)xmin;
            }
            if (_legacy.TryGetValue("YMin", out var ymin)
                && _legacy.TryGetValue("YMax", out var ymax))
            {
                Depth = (double)ymax - (double)ymin;
            }
        }
    }

    public class HelixParameters
    {
        public double LayerHeight { get; set; } = 4.0;
        // StartAngle is no longer exposed in the SettingsDialog (Issue #16
        // — was struck through as unused) but the spiral interpolator still
        // reads it, so the field stays here. Default 0 means "start the
        // spiral at the contour's natural curve start"; that's been the
        // de-facto behavior since the dialog never had a useful preset.
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
        public bool OuterWallBracing { get; set; } = false;

        // Number of times the bracing toolpath touches the outer wall
        // around each layer when OuterWallBracing is on. Visually = the
        // number of "kisses" the bracing makes with the wall, countable
        // by eye in the viewport. The generator samples 2× this many
        // points internally (alternating outer/inner) so each touch pairs
        // with one inner anchor. Decoupled from FramesPerLayer (Issue #11);
        // range 4..500 enforced at the UI.
        public int BracingContactPoints { get; set; } = 60;

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
