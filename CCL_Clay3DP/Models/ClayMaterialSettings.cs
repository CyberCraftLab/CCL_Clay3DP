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

        // Water added during clay prep, as percentage of dry clay mass
        // (additive — 6% = 60 g water per 1 kg dry clay, standard pottery
        // convention). Recorded only in this slice; downstream behavior
        // (nozzle recommendation, feed/spindle adjustment) wired in later
        // slices once the 6/8/10/12% experiments produce a defensible curve.
        public double WaterPercent { get; set; } = 0.0;

        // Shrinkage compensation: when enabled, the slice pipeline scales
        // the input geometry uniformly (X=Y=Z) by 1/(1 - ShrinkagePercent/100)
        // about the part footprint centroid on Z=0, so the printed (and later
        // shrunk) part lands at the user's intended size. ShrinkagePercent is
        // total combined shrinkage (drying + firing) — typical stoneware
        // 10-13%. Toggle off = no scaling, original behavior.
        public bool EnableShrinkageCompensation { get; set; } = false;
        public double ShrinkagePercent { get; set; } = 0.0;

        public ClayMaterialSettings Clone()
        {
            return new ClayMaterialSettings
            {
                PresetName = PresetName,
                BeadDiameter = BeadDiameter,
                MaxOverhangAngle = MaxOverhangAngle,
                MinLayerBondRatio = MinLayerBondRatio,
                MaterialDensity = MaterialDensity,
                WaterPercent = WaterPercent,
                EnableShrinkageCompensation = EnableShrinkageCompensation,
                ShrinkagePercent = ShrinkagePercent,
            };
        }
    }
}
