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

namespace CCL_Clay3DP.Models
{
    /// <summary>
    /// Derived geometric properties of an extruded clay bead.
    ///
    /// A nominally-circular bead of diameter D, squashed to layer height
    /// H during deposition, conserves its cross-sectional area:
    ///
    ///     π·(D/2)²  =  π·(W/2)·(H/2)        →    W = D² / H
    ///
    /// When H == D the bead stays circular (W = D). H > D is physically
    /// impossible (the bead can't span a vertical gap larger than its
    /// own diameter) and is rejected upstream before this helper runs.
    ///
    /// Single source of truth so the elliptical-tube preview, the outer-
    /// wall bracing kiss offset (Issue #11), and any future bead-aware
    /// path math all agree on the same width.
    /// </summary>
    public static class ClayBeadGeometry
    {
        /// <summary>
        /// Full deposited bead width in mm, given nominal bead diameter
        /// (= nozzle diameter, by convention) and layer height.
        /// </summary>
        public static double ComputeWidth(double diameter, double layerHeight)
        {
            if (diameter <= 0)
                throw new ArgumentException("diameter must be positive", nameof(diameter));
            if (layerHeight <= 0)
                throw new ArgumentException("layer height must be positive", nameof(layerHeight));

            // Circular case (within tolerance) — no squish, width == diameter.
            if (Math.Abs(layerHeight - diameter) < 1e-6) return diameter;
            return (diameter * diameter) / layerHeight;
        }
    }
}
