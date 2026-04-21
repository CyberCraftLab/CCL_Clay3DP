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

using System;
using Rhino.Geometry;

namespace CCL_Clay3DP.Zigzag
{
    public static class MeshOffsetter
    {
        /// <summary>
        /// Produce a mesh offset inward from the input surface by <paramref name="thickness"/>.
        /// If input is a Brep it is first meshed. Normals are unified outward and then
        /// the mesh is offset inward. Verifies the offset shrank (not grew) the bbox; if
        /// the direction flipped, retries with the opposite sign.
        /// </summary>
        public static Mesh OffsetInward(Brep brep, Mesh mesh, double thickness)
        {
            if (thickness <= 0)
                throw new Exception("Shell thickness must be positive");

            Mesh outer = mesh != null
                ? mesh.DuplicateMesh()
                : MeshFromBrep(brep);

            if (outer == null || !outer.IsValid)
                throw new Exception("Failed to obtain a valid mesh for offsetting");

            outer.Normals.ComputeNormals();
            outer.UnifyNormals();
            outer.Normals.ComputeNormals();

            var innerBox = outer.GetBoundingBox(true);
            double baseDiag = innerBox.Diagonal.Length;

            // Mesh.Offset(positive) typically moves along +normals (outward for unified
            // meshes). We want inward, so negate. If the resulting bbox is larger than
            // the input's, the convention was the opposite — flip and retry once.
            Mesh offset = outer.Offset(-thickness);
            if (offset == null)
                offset = outer.Offset(thickness);

            if (offset == null)
                throw new Exception("Mesh.Offset returned null");

            var offBox = offset.GetBoundingBox(true);
            if (offBox.Diagonal.Length > baseDiag + 1e-6)
            {
                var retry = outer.Offset(thickness);
                if (retry != null && retry.GetBoundingBox(true).Diagonal.Length < baseDiag + 1e-6)
                    offset = retry;
            }

            if (!offset.IsValid)
                throw new Exception("Offset mesh is invalid — shell thickness may be too large");

            return offset;
        }

        private static Mesh MeshFromBrep(Brep brep)
        {
            if (brep == null) return null;
            var parts = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
            if (parts == null || parts.Length == 0) return null;
            var joined = new Mesh();
            foreach (var p in parts) joined.Append(p);
            return joined;
        }
    }
}
