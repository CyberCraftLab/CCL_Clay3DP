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

namespace CCL_Clay3DP.Models
{
    /// <summary>
    /// User-configurable base ("raft") for closed-loop / vase-style parts.
    /// A base is N layers of skirt + contour + 45-degree cross-hatch infill
    /// printed before the part body, intended to give better adhesion and
    /// a flat starting surface for parts whose lowest contour is otherwise
    /// hollow.
    ///
    /// Infill pattern is hardcoded to alternating ±45-degree crosshatch
    /// with line spacing equal to the bead diameter. Both choices were
    /// fixed by the project owner as the structurally preferred default,
    /// so they are not exposed as user options. If a concentric-offset
    /// alternative is needed in the future, reintroduce a pattern enum
    /// here and a dropdown in the settings dialog.
    ///
    /// Slice 1 (this commit): settings + persistence only. The toolpath
    /// pipeline does not yet read these values. Slices 2 and 3 wire in
    /// contour generation and infill respectively.
    /// </summary>
    public class BaseSettings
    {
        // Off by default so existing workflows are unchanged. Users opt in
        // from the Settings dialog.
        public bool EnableBase { get; set; } = false;

        // How many base layers to print before the part. Clamped 2..10 in
        // the UI; the runtime should also clamp defensively when consuming
        // imported settings files (Slice 2).
        public int LayerCount { get; set; } = 3;
    }
}
