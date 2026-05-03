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
using System.Text;
using Rhino.Geometry;
using CCL_Clay3DP.Core;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Analysis
{
    /// <summary>
    /// Checks geometry / toolpath bounding boxes against the configured
    /// build volume. Used by the panel to block Send-to-RoboDK and warn
    /// the user when a part won't fit (most commonly after shrinkage
    /// compensation enlarges a borderline part past the workspace).
    /// </summary>
    public static class BuildVolumeCheck
    {
        /// <summary>
        /// Per-axis overflow distances (mm). A value > 0 means the bbox
        /// extends beyond the build volume on that side. Z min is implicit
        /// (the build plate at Z=0) — we only flag Z if the top exceeds
        /// the configured Height.
        /// </summary>
        public class Overflow
        {
            public double XMinUnder { get; set; }   // bbox.MinX < volume.XMin → positive
            public double XMaxOver  { get; set; }   // bbox.MaxX > volume.XMax → positive
            public double YMinUnder { get; set; }   // bbox.MinY < volume.YMin → positive
            public double YMaxOver  { get; set; }   // bbox.MaxY > volume.YMax → positive
            public double ZMinUnder { get; set; }   // bbox.MinZ < 0 → positive
            public double ZMaxOver  { get; set; }   // bbox.MaxZ > volume.Height → positive

            public bool HasOverflow =>
                XMinUnder > 0.0 || XMaxOver > 0.0 ||
                YMinUnder > 0.0 || YMaxOver > 0.0 ||
                ZMinUnder > 0.0 || ZMaxOver > 0.0;

            /// <summary>
            /// Human-readable per-axis description, one line per overflowing
            /// side. Empty string if no overflow.
            /// </summary>
            public string Describe()
            {
                if (!HasOverflow) return string.Empty;
                var sb = new StringBuilder();
                if (XMinUnder > 0.0) sb.AppendLine($"  • X-min:  {XMinUnder:F1} mm past the left wall");
                if (XMaxOver  > 0.0) sb.AppendLine($"  • X-max:  {XMaxOver:F1} mm past the right wall");
                if (YMinUnder > 0.0) sb.AppendLine($"  • Y-min:  {YMinUnder:F1} mm past the front wall");
                if (YMaxOver  > 0.0) sb.AppendLine($"  • Y-max:  {YMaxOver:F1} mm past the back wall");
                if (ZMinUnder > 0.0) sb.AppendLine($"  • Z-min:  {ZMinUnder:F1} mm below the build plate");
                if (ZMaxOver  > 0.0) sb.AppendLine($"  • Z-max:  {ZMaxOver:F1} mm above the height limit");
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// Check a bounding box against the build volume. Returns an
        /// Overflow with all-zero values when the bbox fits.
        /// </summary>
        public static Overflow Check(BoundingBox bbox, BuildVolumeSettings volume)
        {
            var o = new Overflow();
            if (!bbox.IsValid || volume == null) return o;

            if (bbox.Min.X < volume.XMin) o.XMinUnder = volume.XMin - bbox.Min.X;
            if (bbox.Max.X > volume.XMax) o.XMaxOver  = bbox.Max.X - volume.XMax;
            if (bbox.Min.Y < volume.YMin) o.YMinUnder = volume.YMin - bbox.Min.Y;
            if (bbox.Max.Y > volume.YMax) o.YMaxOver  = bbox.Max.Y - volume.YMax;
            if (bbox.Min.Z < 0.0)         o.ZMinUnder = -bbox.Min.Z;
            if (bbox.Max.Z > volume.Height) o.ZMaxOver = bbox.Max.Z - volume.Height;
            return o;
        }

        /// <summary>
        /// Bounding box of a Brep + Mesh selection (whichever is non-null).
        /// Caller passes the post-translate, post-shrinkage selection so the
        /// bbox reflects what the slicer will actually see.
        /// </summary>
        public static BoundingBox SelectionBoundingBox(GeometrySelection sel)
        {
            if (sel == null) return BoundingBox.Unset;
            if (sel.Brep != null) return sel.Brep.GetBoundingBox(true);
            if (sel.Mesh != null) return sel.Mesh.GetBoundingBox(true);
            return BoundingBox.Unset;
        }

        /// <summary>
        /// Bounding box of every robot-visited point in the result —
        /// SkirtFrames + BaseFrames + part Frames combined. This is the
        /// authoritative post-slice check: catches base raft footprints
        /// extending beyond the part, skirt offsets, etc.
        /// </summary>
        public static BoundingBox FrameStreamBoundingBox(SpiralResult result)
        {
            var bbox = BoundingBox.Unset;
            if (result == null) return bbox;
            AddFrameOrigins(ref bbox, result.SkirtFrames);
            AddFrameOrigins(ref bbox, result.BaseFrames);
            AddFrameOrigins(ref bbox, result.Frames);
            return bbox;
        }

        private static void AddFrameOrigins(ref BoundingBox bbox, List<Plane> frames)
        {
            if (frames == null) return;
            foreach (var f in frames)
                bbox.Union(f.Origin);
        }
    }
}
