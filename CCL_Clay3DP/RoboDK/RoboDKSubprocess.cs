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
using System.Diagnostics;
using System.IO;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.RoboDK
{
    public static class RoboDKSubprocess
    {
        /// <summary>
        /// Send toolpath frames to RoboDK by running a Python 3 script
        /// via RoboDK's embedded Python interpreter.
        ///
        /// The script reads the frame JSON, connects to RoboDK, creates
        /// robot targets from each frame, and builds a program.
        /// </summary>
        public static void SendFrames(string framesJsonPath, RobotSettings settings)
        {
            string pythonExe = settings.RoboDKPythonExe;
            string apiPath = settings.RoboDKApiPath;

            if (!File.Exists(pythonExe))
                throw new Exception($"RoboDK Python not found at: {pythonExe}");
            if (!Directory.Exists(apiPath))
                throw new Exception($"RoboDK API not found at: {apiPath}");

            // Build the Python script
            string script = GenerateScript(framesJsonPath, apiPath, settings);

            // Use a unique suffix per run for both the script and log so a
            // previous subprocess that is still executing in the background
            // (e.g. finishing a RoboDK render) doesn't hold an exclusive
            // handle on the paths the new subprocess needs to write to.
            // Without this, cmd.exe's '> logPath' redirection fails fast
            // and we end up reporting an opaque file-lock IOException
            // instead of the real RoboDK error.
            string runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string scriptPath = Path.Combine(Path.GetTempPath(),
                $"ccl_clay3dp_send_{runStamp}.py");
            File.WriteAllText(scriptPath, script);

            // Don't launch RoboDK manually — Robolink() in the Python script
            // will connect to a running instance or start one automatically.

            // Redirect Python output to a log file so the process can run
            // independently without C# having to read its output stream.
            string logPath = Path.Combine(Path.GetTempPath(),
                $"ccl_clay3dp_{runStamp}.log");
            Rhino.RhinoApp.WriteLine($"[CCL_Clay3DP] RoboDK log: {logPath}");

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptPath}\" > \"{logPath}\" 2>&1",
                    UseShellExecute = true, // required for shell redirection
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                },
            };

            // Use cmd.exe to handle the redirection properly
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{pythonExe}\" \"{scriptPath}\" > \"{logPath}\" 2>&1\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            proc.Start();

            // Wait briefly for the station to load, then return.
            // The Python script continues running in the background —
            // it will finish the machining project generation on its own.
            // Any errors will be in the log file.
            bool started = proc.WaitForExit(5000); // wait up to 5s for quick failures
            if (started && proc.ExitCode != 0)
            {
                string log = ReadLogSafely(logPath);
                throw new Exception(
                    $"RoboDK script failed (exit {proc.ExitCode}).\n" +
                    $"Log: {logPath}\n{log}");
            }

            // Don't delete scriptPath or logPath here — the Python process
            // may still be using them. Each run uses a unique timestamp in
            // the filename so old ones don't interfere.
        }

        // Open the log with shared read/write access so we can still read
        // even while the subprocess holds an exclusive write handle.
        private static string ReadLogSafely(string logPath)
        {
            if (!File.Exists(logPath)) return "(no log file)";
            try
            {
                using (var fs = new FileStream(logPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                return $"(could not read log: {ex.Message})";
            }
        }

        private static string GenerateScript(string framesJsonPath,
            string apiPath, RobotSettings settings)
        {
            // Escape backslashes for Python string literals
            string jsonPathEsc = framesJsonPath.Replace("\\", "\\\\");
            string apiPathEsc = apiPath.Replace("\\", "\\\\");
            string stationEsc = (settings.RoboDKStationTemplatePath ?? "")
                .Replace("\\", "\\\\");
            string projectName = settings.RoboDKProjectName ?? "3DP";
            string robodkExeEsc = (settings.RoboDKExecutablePath ?? "")
                .Replace("\\", "\\\\");

            return $@"
import sys
import json

sys.path.insert(0, r'{apiPathEsc}')
from robodk import robolink, robomath

# Load curve data (6xN format: x,y,z,nx,ny,nz)
with open(r'{jsonPathEsc}', 'r') as f:
    data = json.load(f)

points_6xn = data['points']
feed_rate = data.get('feed_rate', 100.0)       # mm/s, operation speed
travel_speed = data.get('travel_speed', 100.0) # mm/s, approach/retract
spindle_speed = data.get('spindle_speed', 500.0)
nozzle_tool = data.get('nozzle_tool', 'T10')   # T10 / T11 / T12
layer_height = data.get('layer_height', 0.0)   # mm, for template name only

print(f'Loaded {{len(points_6xn)}} points')
print(f'  Operation speed: {{feed_rate}} mm/s')
print(f'  Travel speed: {{travel_speed}} mm/s')
print(f'  Spindle: S{{spindle_speed:.0f}}')
print(f'  Nozzle tool: {{nozzle_tool}}')
print(f'  Layer height: {{layer_height}} mm')

# Connect to running RoboDK or start one if needed
robodk_path = r'{robodkExeEsc}' or None
rdk = robolink.Robolink(robodk_path=robodk_path)
rdk.Render(False)  # disable rendering during import for speed
print('Connected to RoboDK')

# Close any existing station and load our template fresh.
# If RoboDK was left open by a previous Send and the user (or our own
# prior run) modified items, CloseStation() would normally show a
# 'Save changes?' modal that blocks this API call indefinitely. Mark
# the station as saved first so the close is silent — the Rhino-side
# UI has already confirmed with the user that discarding is OK.
station_path = r'{stationEsc}'
if station_path:
    try:
        rdk.setParam('Unsaved', '0')
    except Exception as _e:
        print(f'Note: could not clear Unsaved flag ({{_e}}); CloseStation may prompt')
    rdk.CloseStation()
    rdk.AddFile(station_path)
    print(f'Loaded station: {{station_path}}')

# Find specific station items by name
robot = rdk.Item('KUKA KR 10 R1100-2', robolink.ITEM_TYPE_ROBOT)
if not robot.Valid():
    # Fallback: pick any robot
    robot = rdk.Item('', robolink.ITEM_TYPE_ROBOT)
if not robot.Valid():
    raise RuntimeError('No robot found in the station')
print(f'Using robot: {{robot.Name()}}')

# Tool = BasePlate02 (the build plate attached to the robot)
tool = rdk.Item('BasePlate02', robolink.ITEM_TYPE_TOOL)
if not tool.Valid():
    tool = robot.getLink(robolink.ITEM_TYPE_TOOL)
if tool.Valid():
    print(f'Using tool: {{tool.Name()}}')
else:
    print('Warning: tool BasePlate02 not found')

# Reference frame = selected nozzle tool (T10/T11/T12 from plugin settings)
ref_frame = rdk.Item(nozzle_tool, robolink.ITEM_TYPE_FRAME)
if not ref_frame.Valid():
    ref_frame = robot.Parent()
    print(f'Warning: {{nozzle_tool}} not found, using {{ref_frame.Name()}}')
else:
    print(f'Using reference frame: {{ref_frame.Name()}}')

# --- Step 1: Add the spiral as a curve object ---
curve_name = '{projectName}_SpiralCurve'

# Remove old curve if it exists
old_curve = rdk.Item(curve_name, robolink.ITEM_TYPE_OBJECT)
if old_curve.Valid():
    old_curve.Delete()

# Add curve with normals (no projection — our normals are already correct)
curve_object = rdk.AddCurve(points_6xn, reference_object=0,
                            add_to_ref=False,
                            projection_type=robolink.PROJECTION_NONE)
curve_object.setName(curve_name)
# Place curve under BasePlate02 in the station tree
if tool.Valid():
    curve_object.setParent(tool)
    print(f'Placed curve under: {{tool.Name()}}')
else:
    curve_object.setParent(ref_frame)
print(f'Added curve: {{curve_name}} ({{len(points_6xn)}} points)')

# --- Step 2: Use the pre-configured machining template from the station ---
# The station must contain a machining project named '3DP_Template' with
# all correct settings: approach/retract speed, spindle S500, post processor,
# Robot Holds Workpiece, reference frame, tool, etc.
template = rdk.Item('3DP_Template', robolink.ITEM_TYPE_MACHINING)
if not template.Valid():
    raise RuntimeError(
        '3DP_Template machining project not found in station. '
        'Create one in 3DP_v0.4.rdk with the correct settings '
        '(approach speed, retract speed, spindle S500, Robot Holds Workpiece).')

print(f'Found template: {{template.Name()}}')

# Rename the template to encode the run's feed/spindle/nozzle settings,
# e.g. '3DP_T11_F50_S500'. The rename is in-memory only — the next run
# reloads {projectName}_v*.rdk fresh via CloseStation/AddFile, so the
# on-disk name '3DP_Template' is preserved (provided the user does not
# hit Save in RoboDK).
new_template_name = f'{projectName}_{{nozzle_tool}}_F{{feed_rate:.0f}}_S{{spindle_speed:.0f}}_LH{{layer_height:g}}'

# Guard: if an item with the target name already exists (from a stale
# prior run where the station wasn't reloaded, or from the user having
# saved a renamed template into the .rdk file), delete it first so we
# don't end up with two station items sharing the same name — RoboDK
# allows name duplicates and Item() lookups on that name become
# non-deterministic, which would silently break a later Send. At this
# point `template` still has its original name '3DP_Template', so any
# hit on the new name is by definition a different item.
for stale_type in (robolink.ITEM_TYPE_MACHINING, robolink.ITEM_TYPE_PROGRAM):
    stale = rdk.Item(new_template_name, stale_type)
    if stale.Valid():
        print(f'Removing stale item with target name: {{new_template_name}}')
        stale.Delete()

template.setName(new_template_name)
print(f'Renamed template: {{new_template_name}}')

# Update the template's reference frame to the selected nozzle tool
template.setPoseFrame(ref_frame)
print(f'Template reference frame set to: {{ref_frame.Name()}}')

# Update the template's tool
if tool.Valid():
    template.setPoseTool(tool)

# --- Step 3: Link the new curve to the template and regenerate ---
# Use No_Update to prevent auto-solve before we set all project parameters.
prog, status = template.setMachiningParameters('', curve_object, 'No_Update')

# Set machining parameters via the 'Machining' param dict.
# Ref: C:/RoboDK/Library/Macros/Edit_Machining_Settings.py
# - ApproachRetractAll=0 → unchecks 'Aproach/Retract each curve'
# - SpeedOperation       → Operation speed (mm/s)
# - SpeedRapid           → Approach/retract speed (mm/s), default 1000
template.setParam('Machining', {{
    'ApproachRetractAll': 0,          # uncheck ""Aproach/Retract each curve""
    'SpeedOperation': feed_rate,      # operation speed from plugin settings
    'SpeedRapid': travel_speed,       # approach/retract speed from plugin settings
    'RapidApproachRetract': 1,        # approach/retract as MoveJ (joint) not MoveL
}})
print(f'Machining: SpeedOp={{feed_rate}} SpeedRapid={{travel_speed}} RapidApproachRetract=1 ApproachRetractAll=0')

# Program events: inject spindle on/off around the curve follow
# CallPathStart fires between approach and first curve point
# CallPathFinish fires between last curve point and retract
template.setParam('ProgEvents', {{
    'CallPathStart': f'S{{spindle_speed:.0f}}',
    'CallPathStartOn': 1,
    'CallPathFinish': 'S1',
    'CallPathFinishOn': 1,
    'Rounding': 0,       # blending radius (zone data) = 0
    'RoundingOn': 0,     # disable rounding so post's setZoneData is not called
}})
print(f'ProgEvents: start=S{{spindle_speed:.0f}} finish=S1 rounding=off')

# Now solve the path
template.Update()

# Rename the generated program to match the renamed template so the
# RoboDK tree clearly identifies which run produced which program.
if prog is not None and prog.Valid():
    prog.setName(new_template_name)
    print(f'Renamed program: {{new_template_name}}')

rdk.Render(True)

if status == 0:
    print(f'Program generated successfully from template')
else:
    print(f'Program generated with status {{status}} (check RoboDK for warnings)')

print('Done')
";
        }
    }
}
