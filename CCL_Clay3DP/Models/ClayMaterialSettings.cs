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

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace CCL_Clay3DP.Models
{
    public class ClayMaterialSettings
    {
        public string PresetName { get; set; } = "Stoneware";
        public double BeadDiameter { get; set; } = 3.0;
        public double MaxOverhangAngle { get; set; } = 20.0;
        public double MinLayerBondRatio { get; set; } = 0.5;
        public double MaterialDensity { get; set; } = 2.2;

        public ClayMaterialSettings Clone()
        {
            return new ClayMaterialSettings
            {
                PresetName = PresetName,
                BeadDiameter = BeadDiameter,
                MaxOverhangAngle = MaxOverhangAngle,
                MinLayerBondRatio = MinLayerBondRatio,
                MaterialDensity = MaterialDensity,
            };
        }
    }
}
