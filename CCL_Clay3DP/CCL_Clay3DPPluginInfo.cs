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
using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;

[assembly: Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
[assembly: PlugInDescription(DescriptionType.Organization, "CYARC")]

namespace CCL_Clay3DP
{
    public class CCL_Clay3DPInfo : PlugIn
    {
        public static CCL_Clay3DPInfo Instance { get; private set; }

        public static readonly Guid PanelId = new Guid("b3a7c1d2-4e5f-6a7b-8c9d-0e1f2a3b4c5d");

        public CCL_Clay3DPInfo()
        {
            Instance = this;
        }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            RhinoApp.WriteLine("CCL_Clay3DP plugin loaded.");

            var panelType = typeof(UI.CCL_Clay3DPPanel);
            var icon = UI.PluginIcon.Create();
            if (icon != null)
                RhinoApp.WriteLine($"CCL_Clay3DP: icon created ({icon.Width}x{icon.Height}).");
            else
                RhinoApp.WriteLine("CCL_Clay3DP: icon is null — tab will have no image.");
            Rhino.UI.Panels.RegisterPanel(this, panelType, "CCL_Clay3DP", icon);

            return LoadReturnCode.Success;
        }
    }
}
