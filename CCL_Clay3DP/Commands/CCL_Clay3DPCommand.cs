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
using Rhino;
using Rhino.Commands;

namespace CCL_Clay3DP.Commands
{
    public class CCL_Clay3DPCommand : Command
    {
        public CCL_Clay3DPCommand()
        {
            Instance = this;
        }

        public static CCL_Clay3DPCommand Instance { get; private set; }

        public override string EnglishName => "CCL_Clay3DP";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var panelId = CCL_Clay3DPInfo.PanelId;
            var visible = Rhino.UI.Panels.IsPanelVisible(panelId);
            if (visible)
                Rhino.UI.Panels.ClosePanel(panelId);
            else
                Rhino.UI.Panels.OpenPanel(panelId);

            return Result.Success;
        }
    }
}
