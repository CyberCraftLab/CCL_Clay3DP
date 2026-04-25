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
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Core
{
    /// <summary>
    /// Geometry-shape gate for features that only behave on ruled /
    /// extrudable surfaces. Used by Outer Wall Bracing — its per-layer
    /// DivideByCount + seam-align algorithm wanders between layers on
    /// free-form geometry, producing a twisted bracing structure that
    /// does not print. We refuse to run bracing on geometry that fails
    /// this gate so users don't waste time discovering the limit.
    ///
    /// "Ruled" = the surface has at least one straight parametric
    /// direction at every point. Includes:
    ///   - Planar surfaces (any direction is straight)
    ///   - Cylinders / prisms / extrusions (axial direction is straight)
    ///   - Cones (rulings from apex to base are straight)
    /// Excludes:
    ///   - Spheres, ellipsoids, tori
    ///   - Organic free-form Breps (sweep / loft / patch surfaces)
    ///   - Anything with double curvature
    /// </summary>
    public static class GeometryCurvature
    {
        /// <summary>
        /// True if every NURBS face of the Brep is ruled or planar.
        /// For a Mesh selection we cannot reliably test curvature in
        /// the available time budget, so we accept it (trust the user).
        /// Future work: estimate per-vertex principal curvatures and
        /// reject when both are non-trivially non-zero.
        /// </summary>
        public static bool IsRuled(GeometrySelection sel)
        {
            if (sel == null) return false;
            if (sel.Brep != null) return IsRuledBrep(sel.Brep);
            if (sel.Mesh != null) return true;
            return false;
        }

        private static bool IsRuledBrep(Brep brep)
        {
            if (brep == null || brep.Faces.Count == 0) return false;
            foreach (var face in brep.Faces)
            {
                var srf = face.UnderlyingSurface();
                if (srf == null) continue;
                if (!IsRuledSurface(srf)) return false;
            }
            return true;
        }

        private static bool IsRuledSurface(Surface srf)
        {
            if (srf == null) return false;

            // Planar = trivially ruled (every direction is straight).
            if (srf.IsPlanar()) return true;

            // For NURBS surfaces, a parametric direction with degree 1
            // is straight (linear interpolation between control rows).
            // If at least one direction is degree 1, the surface is
            // ruled in that direction. Cylinders, cones, prisms,
            // extrusions all show up as degree 1 in their axial /
            // ruling direction.
            int degU = srf.Degree(0);
            int degV = srf.Degree(1);
            return degU == 1 || degV == 1;
        }
    }
}
