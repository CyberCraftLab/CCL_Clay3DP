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
using System.Drawing;

namespace CCL_Clay3DP.Models
{
    /// <summary>
    /// PBR material parameters for previewing the printed clay model in
    /// Rhino's Shaded / Rendered display modes. Values target the **wet
    /// greenware** state (what just came out of the extruder) — not the
    /// fired or glazed appearance.
    ///
    /// Hard-coded on purpose: users select a clay preset in Settings, the
    /// matching preview material follows automatically. No PBR fields
    /// pollute the saved settings JSON.
    /// </summary>
    public static class ClayPreviewMaterials
    {
        public class ClayPbr
        {
            /// <summary>Greenware base color (sRGB).</summary>
            public Color BaseColor;
            /// <summary>0 = mirror, 1 = totally diffuse. Wet clay is very matte.</summary>
            public double Roughness;
            /// <summary>0 for opaque clays, &gt;0 only for porcelain (translucent in thin sections).</summary>
            public double Subsurface;
            /// <summary>Tint of the light that re-emerges from sub-surface scattering.</summary>
            public Color SubsurfaceColor;
            /// <summary>Notes on the physical clay this preset represents.</summary>
            public string Notes;
        }

        public static readonly Dictionary<string, ClayPbr> All =
            new Dictionary<string, ClayPbr>
            {
                ["Porcelain"] = new ClayPbr
                {
                    BaseColor = Color.FromArgb(245, 235, 220),
                    Roughness = 0.85,
                    Subsurface = 0.15,
                    SubsurfaceColor = Color.FromArgb(250, 220, 200),
                    Notes =
                        "High-fire (1200-1400°C). Density ~2.3-2.5 g/cm³. Low " +
                        "plasticity, very fine grain. Compressive strength fired " +
                        "280-700 MPa. Shrinkage 12-18%. Distinctive thin-section " +
                        "translucency drives the small subsurface value.",
                },
                ["Stoneware"] = new ClayPbr
                {
                    BaseColor = Color.FromArgb(165, 140, 115),
                    Roughness = 0.85,
                    Subsurface = 0.0,
                    SubsurfaceColor = Color.FromArgb(165, 140, 115),
                    Notes =
                        "Mid-fire (1200-1300°C). CCL-ALTAR-01 default. Density " +
                        "~2.2-2.4 g/cm³. High plasticity, common throwing/printing " +
                        "body. Compressive strength fired 150-300 MPa. Shrinkage " +
                        "10-13%. Opaque.",
                },
                ["Earthenware"] = new ClayPbr
                {
                    BaseColor = Color.FromArgb(180, 100, 70),
                    Roughness = 0.9,
                    Subsurface = 0.0,
                    SubsurfaceColor = Color.FromArgb(180, 100, 70),
                    Notes =
                        "Low-fire (950-1150°C). Terracotta from iron oxide. Density " +
                        "~2.0-2.2 g/cm³. High plasticity, sandy texture. Porous " +
                        "after firing. Compressive strength fired 50-100 MPa. " +
                        "Shrinkage 5-10%.",
                },
            };

        /// <summary>
        /// Look up the PBR data for a preset by name. Falls back to Stoneware
        /// (the lab default) for any unrecognised name including "Custom".
        /// </summary>
        public static ClayPbr Get(string presetName)
        {
            if (presetName != null && All.TryGetValue(presetName, out var pbr))
                return pbr;
            return All["Stoneware"];
        }
    }
}
