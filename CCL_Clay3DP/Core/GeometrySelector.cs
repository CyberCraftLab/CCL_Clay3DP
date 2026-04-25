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

using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Core
{
    public static class GeometrySelector
    {
        /// <summary>
        /// Prompt user to select a single Brep, Surface, or Mesh.
        /// Returns null if the user cancels.
        /// </summary>
        public static GeometrySelection Select()
        {
            var go = new GetObject();
            go.SetCommandPrompt("Select geometry for spiral slicing");
            go.GeometryFilter = ObjectType.Brep | ObjectType.Surface | ObjectType.Mesh;
            go.SubObjectSelect = false;
            go.Get();

            if (go.CommandResult() != Result.Success)
                return null;

            var objRef = go.Object(0);
            if (objRef == null)
                return null;

            var result = new GeometrySelection
            {
                SourceObjectId = objRef.ObjectId,
            };

            var brep = objRef.Brep();
            if (brep != null)
            {
                result.Brep = brep;
                RhinoApp.WriteLine($"Selected Brep with {brep.Faces.Count} face(s)");
                return result;
            }

            var surface = objRef.Surface();
            if (surface != null)
            {
                result.Brep = surface.ToBrep();
                RhinoApp.WriteLine("Selected Surface (converted to Brep)");
                return result;
            }

            var mesh = objRef.Mesh();
            if (mesh != null)
            {
                result.Mesh = mesh;
                RhinoApp.WriteLine($"Selected Mesh with {mesh.Faces.Count} face(s)");
                return result;
            }

            return null;
        }
    }
}
