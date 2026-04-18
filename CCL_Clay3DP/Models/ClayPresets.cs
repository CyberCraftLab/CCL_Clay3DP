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

namespace CCL_Clay3DP.Models
{
    public static class ClayPresets
    {
        public static readonly Dictionary<string, ClayMaterialSettings> All =
            new Dictionary<string, ClayMaterialSettings>
            {
                ["Porcelain"] = new ClayMaterialSettings
                {
                    PresetName = "Porcelain",
                    BeadDiameter = 2.0,
                    MaxOverhangAngle = 15.0,
                    MinLayerBondRatio = 0.6,
                    MaterialDensity = 2.4,
                },
                ["Stoneware"] = new ClayMaterialSettings
                {
                    PresetName = "Stoneware",
                    BeadDiameter = 3.0,
                    MaxOverhangAngle = 20.0,
                    MinLayerBondRatio = 0.5,
                    MaterialDensity = 2.2,
                },
                ["Earthenware"] = new ClayMaterialSettings
                {
                    PresetName = "Earthenware",
                    BeadDiameter = 5.0,
                    MaxOverhangAngle = 25.0,
                    MinLayerBondRatio = 0.4,
                    MaterialDensity = 1.8,
                },
            };

        public static IEnumerable<string> Names
        {
            get
            {
                yield return "Porcelain";
                yield return "Stoneware";
                yield return "Earthenware";
                yield return "Custom";
            }
        }

        public static ClayMaterialSettings Get(string name)
        {
            if (All.TryGetValue(name, out var preset))
                return preset.Clone();
            return new ClayMaterialSettings { PresetName = "Custom" };
        }
    }
}
