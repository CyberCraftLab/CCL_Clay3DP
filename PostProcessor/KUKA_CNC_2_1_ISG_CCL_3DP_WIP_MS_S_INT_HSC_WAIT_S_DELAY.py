# Copyright 2020 - RoboDK Inc. - https://robodk.com/
# Modifications Copyright 2026 CyberCraft Lab, OTH Regensburg,
#                              Prof. Christophe Barlieb
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
# http://www.apache.org/licenses/LICENSE-2.0
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

# ----------------------------------------------------
# KUKA CNC 2.1 ISG Kernel PP
# with CyberCraft Lab (CCL) customisations for clay 3D printing
# on a KUKA KR 10 R1100-2 with a fixed Stoneflower paste extruder.
#
# Modifications by CyberCraft Lab, OTH Regensburg (2026):
#   - ProgStart / ProgFinish: CCL / OTH branding comments
#   - setFrame: capture the reference-frame name in CURRENT_FRAME_NAME
#     so the tool change can reflect which nozzle TCP is in use
#   - setTool: emit "T<N> M6" tool change derived from the reference
#     frame name (T10 / T11 / T12 map directly to KUKA tool numbers)
#   - set_move_type: scope #HSC ON [BSPLINE...] to MoveL (curve-follow)
#     sections only, and emit #HSC OFF before any MCS transition;
#     prevents KUKA CNC error 120102 (cannot flush channel while
#     spline interpolation is active)
#   - setZoneData: silenced (no-op) because KUKA CNC ISG handles
#     blending via #HSC BSPLINE, not per-instruction zone data
#   - Extruder lead compensation: buffers extrusion-on linear segments
#     and retroactively moves the S1 (extruder-off) command earlier by
#     EXTRUDER_OFF_DELAY_S seconds so the nozzle actually stops at the
#     intended end-of-path position (clay keeps flowing under pressure
#     for ~0.25 s after the command is issued)
#   - Retract spindle lookahead: when a MoveJ (rapid) arrives while the
#     extruder is still on, the post backs the S1 up by
#     RETRACT_LOOKAHEAD_POINTS MoveL waypoints (so flow has stopped
#     before the nozzle lifts off the print) and stashes the active S
#     value. The next MoveL after the retract re-issues that S value,
#     so the extruder is flowing again by the time the plunge completes
#     and the first print point fires. Source G-code can leave the
#     spindle on continuously with one M3 S<n> at program start.
#   - Spindle decimal stripping: S<number>.0 -> S<number> for clean
#     G-code output
#   - MoveJ rapids emitted as G01 (linear) at F3000 (50 mm/s) instead
#     of G00, so approach/retract moves stay controlled and paste flow
#     stays predictable — see RAPID_FEED_MM_MIN
#   - Various other minor tweaks (see commit history in the public
#     repository for details)
# ----------------------------------------------------
#
# To edit/test this POST PROCESSOR script file:
# Select "Program"->"Add/Edit Post Processor", then select your post or create a new one.
# You can edit this file using any text editor or Python editor. Using a Python editor allows to quickly evaluate a sample program at the end of this file.
# Python should be automatically installed with RoboDK
#
# You can also edit the POST PROCESSOR manually:
#    1- Open the *.py file with Python IDLE (right click -> Edit with IDLE)
#    2- Make the necessary changes
#    3- Run the file to open Python Shell: Run -> Run module (F5 by default)
#    4- The "test_post()" function is called automatically
# Alternatively, you can edit this file using a text editor and run it with Python
#
# To use a POST PROCESSOR file you must place the *.py file in "C:/RoboDK/Posts/"
# To select one POST PROCESSOR for your robot in RoboDK you must follow these steps:
#    1- Open the robot panel (double click a robot)
#    2- Select "Parameters"
#    3- Select "Unlock advanced options"
#    4- Select your post as the file name in the "Robot brand" box
#
# To delete an existing POST PROCESSOR script, simply delete this file (.py file)
#
# ----------------------------------------------------
# More information about RoboDK Post Processors and Offline Programming here:
#     https://robodk.com/help#PostProcessor
#     https://robodk.com/doc/en/PythonAPI/postprocessor.html
# ----------------------------------------------------


# ----------------------------------------------------
# Import RoboDK tools
from robodk import *
import re
import math

JOINT_DATA = ['X','Y','Z','A','B','C','X1=','Y1=','Z1=']

# ----------------------------------------------------
def pose_2_str(pose, joints = None):
    """Prints a pose target"""
    [x,y,z,r,p,w] = Pose_2_KUKA(pose)        
    str_xyzwpr = 'X%.4f Y%.4f Z%.4f A%.4f B%.4f C%.4f' % (x,y,z,r,p,w)
    if joints is not None:        
        if len(joints) > 6:
            for j in range(6,len(joints)):
                str_xyzwpr += (' %s%.6f ' % (JOINT_DATA[j], joints[j]))
    
    return str_xyzwpr
    
def joints_2_str(joints):
    """Prints a joint target"""
    str = ''
    for i in range(len(joints)):
        str = str + ('%s%.6f ' % (JOINT_DATA[i], joints[i]))
    str = str[:-1]
    return str

def circle_information(p1,p2,p3):
    v1 = subs3(p2,p1)
    v2 = subs3(p3,p1)
    v1v1 = dot(v1,v1)
    v2v2 = dot(v2,v2)
    v1v2 = dot(v1,v2)
    base = 0.5/(v1v1*v2v2-v1v2*v1v2)
    k1 = base*v2v2*(v1v1-v1v2)
    k2 = base*v1v1*(v2v2-v1v2)
    center = add3(p1,add3(mult3(v1,k1), mult3(v2,k2)))
    radius = norm(subs3(center,p1))
    arc_len = radius*angle3(normalize3(subs3(p1,center)),normalize3(subs3(p3,center)))
    return center, radius, arc_len
    
# ----------------------------------------------------    
# Object class that handles the robot instructions/syntax
class RobotPost(object):
    """Robot post object"""
    PROG_EXT = 'nc'        # Set the program extension
    
    # Other variables
    ROBOT_POST = ''
    ROBOT_NAME = ''
    PROG_FILES = []
    
    PROG = []
    LOG = ''
    nAxes = 6
    nId = 0
    SPEED_F = ' F3000' # Set default speed
    MoveType = -1

    # ------------------ Extruder OFF lead compensation ------------------
    # Delay between S1 command and actual extrusion stop (seconds).
    # Tune this based on your system (clay systems often 0.05–0.40s, but can be higher).
    EXTRUDER_OFF_DELAY_S = 0.25

    # ------------------ Retract spindle lookahead ------------------
    # Number of MoveL waypoints to back the S1 command up when a retract
    # (MoveJ while extruder is on) is detected. Clay flow tapers over
    # the last several print points; backing the S1 up by ~10 points
    # has the bead pressure-drop complete before the lift-off move.
    RETRACT_LOOKAHEAD_POINTS = 10

    # Internal state for lead compensation
    _EXTRUDER_ON = False
    _LAST_XYZ = None              # last cartesian XYZ in program coordinates
    _LAST_F_MM_MIN = 3000.0       # last feed in mm/min (used if no new F is provided)
    _EXTRUDE_SEGS = []            # buffered extrusion-on linear segments for backtracking
    _LAST_EXTRUDER_S = 500.0      # last S value with S > 1 (= resume value after a retract)
    _PENDING_RESUME_S = None      # if set, next MoveL emits this S value to resume after retract
    
    MOVE_TYPE_NONE = -1
    MOVE_NAMES = []
    MOVE_TYPE_MCS = 0
    MOVE_NAMES.append("MCS")
    MOVE_TYPE_HSC = 1
    MOVE_NAMES.append("HSC")
    MOVE_TYPE_PTP = 2
    MOVE_NAMES.append("PTP")
    
    
    REF_FRAME = eye(4)
    
    # Account for the travel along the path in Cartesian space using the P value
    # This allows synchronizing 2 robots
    # Use a call to SYNC_ON (or a comment) to activate or reset
    # Use a call to SYNC_OFF (or a comment) to turn off P output
    P_VALUE = None
    
    # Remember last target values (pose, joints and configuration)
    LAST_POSE = None
    LAST_JOINTS = None
    LAST_CONFIG = None
    
    def __init__(self, robotpost=None, robotname=None, robot_axes = 6, **kwargs):
        self.ROBOT_POST = robotpost
        self.ROBOT_NAME = robotname
        self.PROG = []
        self.LOG = ''
        self.nAxes = robot_axes
        for k,v in kwargs.items():
            if k == 'lines_x_prog':
                self.PROG_MOVE_COUNT_MAX = v
            elif k == 'pose_turntable':
                pose_turntable = v
                # self.TURNTABLE_POSE = pose_turntable
            elif k == 'pose_rail':
                pose_rail = v
                # self.RAIL_POSE = pose_rail      
        
    def ProgStart(self, progname):
        self.addline('; program: %s()' % progname, False)
        self.addline('; CyberCraft Lab, OTH Regensburg', False)
        self.addline('', False)
        self.addline('G90')
        self.addline('', False)
        self.addline('#CS OFF ALL')
        self.addline('#CS DEF[1][0.0000,0.0000,0.0000,0.0000,0.0000,0.0000]')
        self.addline('#CS ON[1]')
        # Note: #HSC ON is emitted by set_move_type() around MoveL sections
        # only, NOT globally. Keeping HSC scoped avoids KUKA CNC error 120102
        # when leaving MCS mode during rapid approach/retract.

    def ProgFinish(self, progname):
        # set_move_type(NONE) will emit #HSC OFF or #MCS OFF as needed
        self.set_move_type(self.MOVE_TYPE_NONE)
        self.addline('', False)
        self.addline('M5')
        self.addline('#TRAFO OFF')
        self.addline('#CS OFF ALL')
        self.addline('M30')
        
    def ProgSave(self, folder, progname, ask_user=False, show_result=False):
        progname = progname + '.' + self.PROG_EXT
        if ask_user or not DirExists(folder):
            filesave = getSaveFile(folder, progname, 'Save program as...')
            if filesave is not None:
                filesave = filesave.name
            else:
                return
        else:
            filesave = folder + '/' + progname
        fid = open(filesave, "w")
        # Renumber N-words sequentially (allows us to insert lines for lead compensation)
        nid = 0
        for line in self.PROG:
            if line.startswith('N'):
                line2 = re.sub(r'^N\d+\s+', '', line)
                nid += 1
                line = 'N%i ' % nid + line2
            fid.write(line + '\n')
        fid.close()
        print('SAVED: %s\n' % filesave)
        self.PROG_FILES = filesave
        #---------------------- show result
        if show_result:
            if type(show_result) is str:
                # Open file with provided application
                import subprocess
                p = subprocess.Popen([show_result, filesave])   
            elif type(show_result) is list:
                import subprocess
                p = subprocess.Popen(show_result + [filesave])   
            else:
                # Open file with default application
                import os
                os.startfile(filesave)  
            
            if len(self.LOG) > 0:
                mbox('Program generation LOG:\n\n' + self.LOG)
    
    def ProgSendRobot(self, robot_ip, remote_path, ftp_user, ftp_pass):
        """Send a program to the robot using the provided parameters. This method is executed right after ProgSave if we selected the option "Send Program to Robot".
        The connection parameters must be provided in the robot connection menu of RoboDK"""
        UploadFTP(self.PROG_FILES, robot_ip, remote_path, ftp_user, ftp_pass)
    
    def set_move_type(self, move_type):
        """Emit KUKA CNC mode transitions.
        MCS brackets MoveJ (rapid/joint) sections, HSC BSPLINE brackets MoveL
        (curve-follow) sections. The two modes are mutually exclusive:
        KUKA CNC error 120102 'Kanal leeren bei aktiver Spline-Interpolation'
        is thrown if we try to leave MCS while HSC is still active, so we
        keep HSC scoped to the MoveL region only."""
        if self.MoveType == move_type:
            return

        # Turn OFF the previous mode
        if self.MoveType >= 0:
            if self.MoveType == self.MOVE_TYPE_MCS:
                self.addline("#MCS OFF")
            elif self.MoveType == self.MOVE_TYPE_HSC:
                self.addline("#HSC OFF")
            # PTP doesn't require an explicit OFF
            self.MoveType = -1

        # Turn ON the new mode
        if move_type >= 0:
            if move_type == self.MOVE_TYPE_MCS:
                self.addline("#MCS ON")
            elif move_type == self.MOVE_TYPE_HSC:
                # BSPLINE smoothing only wraps the curve-follow section
                self.addline("#HSC ON [BSPLINE PATH_DEV 0.5000 TRACK_DEV 0.5000]")
            # PTP doesn't require an explicit ON
            self.MoveType = move_type
    # --------- Helpers for Extruder OFF lead compensation ---------
    def _parse_F_mm_min(self, token):
        m = re.search(r'\bF([0-9]+(?:\.[0-9]+)?)\b', token)
        if m:
            try:
                return float(m.group(1))
            except:
                return None
        return None

    def _replace_xyz_in_gline(self, gline, xyz):
        # Replace X.. Y.. Z.. tokens (keeps other tokens like A/B/C/P/F)
        x, y, z = xyz
        gline = re.sub(r'\bX-?[0-9]+(?:\.[0-9]+)?\b', 'X%.4f' % x, gline)
        gline = re.sub(r'\bY-?[0-9]+(?:\.[0-9]+)?\b', 'Y%.4f' % y, gline)
        gline = re.sub(r'\bZ-?[0-9]+(?:\.[0-9]+)?\b', 'Z%.4f' % z, gline)
        return gline

    def _apply_extruder_off_lead(self):
        '''Shift S1 earlier by EXTRUDER_OFF_DELAY_S using buffered extrusion-on segments.
        Inserts an earlier S1 and splits one motion segment if needed.
        Returns True if the S1 was re-positioned (so we should not output S1 at the current location).'''
        if not self._EXTRUDE_SEGS or self.EXTRUDER_OFF_DELAY_S <= 0:
            return False

        d = (max(self._LAST_F_MM_MIN, 0.0) / 60.0) * float(self.EXTRUDER_OFF_DELAY_S)
        if d <= 1e-6:
            return False

        acc = 0.0
        seg_idx = None
        rem_from_end = None

        for i in range(len(self._EXTRUDE_SEGS) - 1, -1, -1):
            seg = self._EXTRUDE_SEGS[i]
            L = seg['len']
            if acc + L >= d:
                seg_idx = i
                rem_from_end = d - acc
                break
            acc += L

        if seg_idx is None:
            seg_idx = 0
            rem_from_end = self._EXTRUDE_SEGS[0]['len']

        seg = self._EXTRUDE_SEGS[seg_idx]
        L = max(seg['len'], 1e-9)
        t = (L - rem_from_end) / L
        t = max(0.0, min(1.0, t))

        p0 = seg['p0']
        p1 = seg['p1']
        split = [
            p0[0] + (p1[0] - p0[0]) * t,
            p0[1] + (p1[1] - p0[1]) * t,
            p0[2] + (p1[2] - p0[2]) * t,
        ]

        line_i = seg['line_index']
        if line_i < 0 or line_i >= len(self.PROG):
            return False

        original_line = self.PROG[line_i]
        self.PROG[line_i] = self._replace_xyz_in_gline(original_line, split)

        remainder_line = self._replace_xyz_in_gline(original_line, p1)

        insert_pos = line_i + 1
        self.PROG.insert(insert_pos, 'S1')
        self.PROG.insert(insert_pos + 1, remainder_line)

        self._EXTRUDE_SEGS = []
        self._EXTRUDER_ON = False
        return True

    def _inject_extruder_off_n_points_back(self):
        """Insert an S1 line into PROG at the position of the MoveL that's
        RETRACT_LOOKAHEAD_POINTS waypoints back from the most recent one.
        Falls back to emitting S1 inline at the current PROG end when the
        buffered segment list is shorter than the lookahead (= we don't
        have enough printed waypoints to back up by N)."""
        N = self.RETRACT_LOOKAHEAD_POINTS
        if not self._EXTRUDE_SEGS or N < 1:
            self.addline('S1')
            self._EXTRUDE_SEGS = []
            return
        idx = max(0, len(self._EXTRUDE_SEGS) - N)
        line_i = self._EXTRUDE_SEGS[idx]['line_index']
        if line_i < 0 or line_i >= len(self.PROG):
            self.addline('S1')
            self._EXTRUDE_SEGS = []
            return
        self.PROG.insert(line_i, 'S1')
        self._EXTRUDE_SEGS = []

    # Feed rate (mm/min) applied to rapid (approach/retract) moves. The
    # stock RoboDK post emits G00 rapids, but for clay extrusion on the
    # CCL-ALTAR-01 cell we need a controlled linear speed on approach and
    # retract so the nozzle does not outrun the paste flow or collide on
    # fast moves. Emitting G01 ... F3000 keeps those moves linear and
    # predictable at 50 mm/s.
    RAPID_FEED_MM_MIN = 3000

    def MoveJ(self, pose, joints, conf_RLF=None):
        """Add a joint movement (emitted as G01 at RAPID_FEED_MM_MIN for
        controlled approach/retract — see RAPID_FEED_MM_MIN above)."""
        # Retract lookahead: a MoveJ arriving while the extruder is on is
        # the lift-off of a retract — back the S1 up by
        # RETRACT_LOOKAHEAD_POINTS so flow has stopped before the nozzle
        # leaves the print. Stash the active S value so the next MoveL
        # (= first print point after the plunge) can resume it.
        if self._EXTRUDER_ON and self._PENDING_RESUME_S is None:
            self._inject_extruder_off_n_points_back()
            self._PENDING_RESUME_S = self._LAST_EXTRUDER_S
            self._EXTRUDER_ON = False

        self.set_move_type(self.MOVE_TYPE_MCS)
        self.addline('G01 ' + joints_2_str(joints) + (' F%d' % self.RAPID_FEED_MM_MIN))

        if pose is not None:
            self.set_move_type(self.MOVE_TYPE_PTP)
            self.addline('G01 ' + pose_2_str(pose, joints) + (' F%d' % self.RAPID_FEED_MM_MIN))
        
    def MoveL(self, pose, joints, conf_RLF=None):
        """Add a linear movement"""
        # Retract resume: first MoveL after a retract sequence. Re-issue
        # the stashed S value before this print point fires so the
        # extruder is flowing again when we land. _EXTRUDER_ON flips back
        # on here too so subsequent MoveL calls buffer for lookahead.
        if self._PENDING_RESUME_S is not None:
            self.addline('S%d' % int(round(self._PENDING_RESUME_S)))
            self._PENDING_RESUME_S = None
            self._EXTRUDER_ON = True

        #-----------------------------------------------------
        # Update the P value to sync two robots (if required)
        sync_p = ''
        if self.P_VALUE is not None:
            if self.LAST_POSE is not None:
                self.P_VALUE += distance(self.LAST_POSE.Pos(), pose.Pos())
            else:
                self.P_VALUE = 0
            sync_p = ' P%.3f' % self.P_VALUE
        #-----------------------------------------------------

        self.set_move_type(self.MOVE_TYPE_HSC)
        # Track feed (mm/min): if a new F is provided it overrides the last one
        _f = self._parse_F_mm_min(self.SPEED_F)
        if _f is not None:
            self._LAST_F_MM_MIN = _f

        # Compute current XYZ for buffering
        _xyz = (self.REF_FRAME*pose).Pos()
        _xyz = [float(_xyz[0]), float(_xyz[1]), float(_xyz[2])]

        self.addline('G01 ' + pose_2_str(self.REF_FRAME*pose, joints) + sync_p + self.SPEED_F)
        self.SPEED_F = ''

        # Buffer extrusion-on linear segments so we can move S1 earlier if needed
        if self._EXTRUDER_ON and self._LAST_XYZ is not None:
            dx = _xyz[0]-self._LAST_XYZ[0]
            dy = _xyz[1]-self._LAST_XYZ[1]
            dz = _xyz[2]-self._LAST_XYZ[2]
            L = math.sqrt(dx*dx + dy*dy + dz*dz)
            self._EXTRUDE_SEGS.append({'p0': self._LAST_XYZ, 'p1': _xyz, 'len': L, 'line_index': len(self.PROG)-1})

        self._LAST_XYZ = _xyz
        
        # Remember the last pose
        self.LAST_POSE = pose
        self.LAST_JOINTS = joints
        self.LAST_CONFIG = conf_RLF      
        
    def MoveC(self, pose1, joints1, pose2, joints2, conf_RLF_1=None, conf_RLF_2=None):
        """Add a circular movement"""
        #-----------------------------------------------------
        # Update the P value to sync two robots (if required)
        sync_p = ''
        if self.P_VALUE is not None:
            center, radius, arc_mm = circle_information(self.LAST_POSE.Pos(), pose1.Pos(), pose2.Pos())
            self.P_VALUE += arc_mm
            sync_p = ' P%.3f' % self.P_VALUE
        #-----------------------------------------------------
        
        xyz1 = (self.REF_FRAME*pose1).Pos()
        xyz2 = (self.REF_FRAME*pose2).Pos()
        self.addline('G02 X%.3f Y%.3f Z%.3f I1=%.3f J1=%.3f K1=%.3f %s' % (xyz2[0], xyz2[1], xyz2[2], xyz1[0], xyz1[1], xyz1[2], sync_p))
        #self.addline('N%02i G102 X%.3f Y%.3f Z%.3f I1=%.3f J1=%.3f K1=%.3f' % (self.nId, xyz1[0], xyz1[1], xyz1[2], xyz2[0]-xyz1[0], xyz2[1]-xyz1[1], xyz2[2]-xyz1[2]))	

        self.LAST_POSE = pose2
        self.LAST_JOINTS = joints2
        self.LAST_CONFIG = conf_RLF_2        
        
    # Holds the last reference-frame name (e.g. 'T10', 'T11', 'T12').
    # Used in setTool to emit the correct tool-change command.
    CURRENT_FRAME_NAME = ''

    def setFrame(self, pose, frame_id=None, frame_name=None):
        """Change the robot reference frame"""
        self.REF_FRAME = pose
        self.CURRENT_FRAME_NAME = frame_name or ''
        self.addline('; Using Reference %s: %s' % (frame_name, pose_2_str(pose)), False)
        self.addline('; (Using absolute coordinates)', False)

    def _frame_tool_number(self):
        """Extract integer tool number from frame name (e.g. 'T10' -> 10).
        Returns None if the frame name doesn't match the T<number> pattern."""
        m = re.match(r'^T(\d+)$', self.CURRENT_FRAME_NAME or '')
        if m:
            return int(m.group(1))
        return None

    def setTool(self, pose, tool_id=None, tool_name=None):
        """Change the robot TCP"""
        self.addline('', False)
        self.addline('; Using Tool %s: %s' % (tool_name, pose_2_str(pose)), False)
        if tool_id < 1:
            tool_id = 99

        self.addline('M5')
        # Emit tool change using the reference frame name (T10/T11/T12)
        tool_num = self._frame_tool_number()
        if tool_num is not None:
            self.addline('T%d M6' % tool_num)
        self.addline('#TRAFO OFF')
        self.addline('#TRAFO ON')
        self.addline('M3 S1')
        #self.addline('G131 100')
        #self.addline('G133 150')
        #self.addline('G134 150')
        self.addline('', False)
        #self.addline('#MCS ON')
        #self.addline('G00 X0.0000 Y-110.0000 Z110.0000 A0.0000 B-20.0000 C0.0000 X1=0.0000')
        #self.addline('G01 A-1 F3000')
        #self.addline('G01 A0')
        #self.addline('#MACHINE DATA SYN [AXNR=4 AXPARAM="lr_param.anwahl_losekomp 1"]')
        #self.addline('#MACHINE DATA SYN [AXNR=4 AXPARAM="getriebe[0].lose        75"]')
        #self.addline('#MACHINE DATA SYN [AXNR=4 AXPARAM="lr_param.n_backlash_cyc 15"]')
        #self.addline('#HSC [BSPLINE PATH_DEV 0.0000 TRACK_DEV 0.0000]')
        #self.addline('#MCS OFF')

    def Pause(self, time_ms):
        """Pause the robot program"""
        # KUKA CNC ISG: '%' is not allowed in the main program.
        # Use standard dwell instead of '% WAIT ...'
        if time_ms < 0:
            # Program stop (operator continue)
            self.addline('M0', False)
        else:
            # Dwell in seconds
            self.addline('G4 X%.3f' % (time_ms*0.001), False) # actually 'X' instead of P!
    
    def setSpeed(self, speed_mms):
        """Changes the robot speed (in mm/s)"""
        self.SPEED_F = ' F%.3f' % (speed_mms*60)
        self._LAST_F_MM_MIN = float(speed_mms*60.0)
        #self.SPEED_F = ' F%.0f' % speed_mms
    
    def setAcceleration(self, accel_mmss):
        """Changes the robot acceleration (in mm/s2)"""
        self.addlog('setAcceleration not defined')
    
    def setSpeedJoints(self, speed_degs):
        """Changes the robot joint speed (in deg/s)"""
        self.addlog('setSpeedJoints not defined')
    
    def setAccelerationJoints(self, accel_degss):
        """Changes the robot joint acceleration (in deg/s2)"""
        self.addlog('setAccelerationJoints not defined')
        
    def setZoneData(self, zone_mm):
        """Changes the rounding radius (aka CNT, APO or zone data) to make the movement smoother.
        Silenced: KUKA CNC ISG handles blending via #HSC settings, not per-instruction."""
        pass

    def setDO(self, io_var, io_value):
        """Sets a variable (digital output) to a given value"""
        if type(io_var) != str:  # Set default variable name if io_var is a number
            io_var = 'OUT[%s]' % str(io_var)
        if type(io_value) != str: # Set default variable value if io_value is a number
            if io_value > 0:
                io_value = 'TRUE'
            else:
                io_value = 'FALSE'

        # At this point, io_var and io_value must be string values
        self.addline('%s=%s' % (io_var, io_value))

    def setAO(self, io_var, io_value):
        """Set an Analog Output"""
        self.setDO(io_var, io_value)
        
    def waitDI(self, io_var, io_value, timeout_ms=-1):
        """Waits for a variable (digital input) io_var to attain a given value io_value. Optionally, a timeout can be provided."""
        if type(io_var) != str:  # Set default variable name if io_var is a number
            io_var = 'IN[%s]' % str(io_var)
        if type(io_value) != str: # Set default variable value if io_value is a number
            if io_value > 0:
                io_value = 'TRUE'
            else:
                io_value = 'FALSE'

        # At this point, io_var and io_value must be string values
        if timeout_ms < 0:
            self.addline('WAIT FOR %s==%s' % (io_var, io_value))
        else:
            self.addline('WAIT FOR %s==%s TIMEOUT=%.1f' % (io_var, io_value, timeout_ms))
        
    def RunCode(self, code, is_function_call = False):
        """Adds code or a function call
        Patches spindle speed formatting so that S<integer>.0 -> S<integer>"""
        def _try_get_S_value(line):
            # Extract first numeric S value in a line (e.g. "S1", "S200", "S500.0")
            try:
                sm = re.search(r'(^|[^A-Za-z0-9_])S\s*([+-]?\d+(?:\.\d+)?)', str(line))
                if not sm:
                    return None
                return float(sm.group(2))
            except:
                return None

        def _strip_spindle_decimal(s):
            # Replace S<number>.0... with S<number>, e.g. S1000.0 -> S1000
            return re.sub(r'(\bS)(-?\d+)(?:\.0+)\b', r'\1\2', s)

        # Extruder state tracking + OFF lead compensation:
        # Convention used in your files: S500 (or any S>1) = extruder ON, S1 = OFF.
        _sval = _try_get_S_value(code)
        if _sval is not None:
            if _sval <= 1.0:
                if self._EXTRUDER_ON:
                    if self._apply_extruder_off_lead():
                        return
                self._EXTRUDER_ON = False
            else:
                self._EXTRUDER_ON = True
                self._LAST_EXTRUDER_S = _sval     # remember for retract resume

        if is_function_call:
            code = code.replace(' ','_')
            if not code.endswith(')'):
                code = code
            code = _strip_spindle_decimal(code)
            self.addline(code)
        else:
            code = _strip_spindle_decimal(code)
            self.addline(code)
        
    def RunMessage(self, message, iscomment = False):
        return
            
        #----------------------------------------------------
        # Check control for P value (not case sensitive, allowed as a comment or TP display)
        # Note:
        #    SYNC_ON will activate or restart synchronization between 2 robots
        #    SYNC_OFF will turn off synchronization between 2 robots
        code_uppercase = message.upper()
        if 'SYNC_ON' in code_uppercase:
            self.P_VALUE = 0
            # Important! Count as 0 at the current location
            if self.LAST_POSE is not None:
                self.MoveL(self.LAST_POSE, self.LAST_JOINTS, self.LAST_CONFIG)
            else:
                # 0 will be automatically added next linear move
                #self.addline('P0')
                pass                
        elif 'SYNC_OFF' in code_uppercase:
            self.P_VALUE = None
        #----------------------------------------------------
        
# ------------------ private ----------------------                
    def addline(self, newline, add_N = True):
        """Add a program line"""
        if add_N:
            self.nId += 1
            newline = 'N%i ' % self.nId + newline
        
        self.PROG.append(newline)
        
    def addlog(self, newline):
        """Add a log message"""
        self.LOG = self.LOG + newline + '\n'

# -------------------------------------------------
# ------------ For testing purposes ---------------   
def Pose(xyzrpw):
    [x,y,z,r,p,w] = xyzrpw
    a = r*math.pi/180
    b = p*math.pi/180
    c = w*math.pi/180
    ca = math.cos(a)
    sa = math.sin(a)
    cb = math.cos(b)
    sb = math.sin(b)
    cc = math.cos(c)
    sc = math.sin(c)
    return Mat([[cb*ca, ca*sc*sb - cc*sa, sc*sa + cc*ca*sb, x],[cb*sa, cc*ca + sc*sb*sa, cc*sb*sa - ca*sc, y],[-sb, cb*sc, cc*cb, z],[0,0,0,1]])
 
def p(xyzrpw):
    return Pose(xyzrpw)

def test_post():
    """Test the post with a basic program"""
    r = RobotPost(r"""KUKA KRC4 KSS8.3 KUKA CNC 2.1 ISG KERNEL""",r"""AGILUS KR10 R1100-2 - KP1-V500""",7,axes_type=['L','L','L','R','R','T','T'], pose_rail=p([2385.610000,5407.971000,2882.627000,-0.000000,-0.000000,-45.000000]))
    r.ProgStart(r"""TestProgram""")
    r.RunMessage(r"""Program generated by cba""", True)
    r.RunMessage(r"""Using nominal kinematics.""", True)
    r.setFrame(p([-1259.013000,-1620.949863,4473.566740,-3.927854,-23.669372,-91.482864]),-1,r"""Machining Frame""")
    r.setTool(p([0.000000,0.000000,88.000000,0.000000,0.000000,0.000000]),-1,r"""Tool 10-88""")
    r.setSpeed(50.000)
    r.setSpeed(100.000)
    r.MoveJ(None,[590.784400,563.603644,824.332191,-175.795035,74.553681,16191.157542,785.000000],None)
    r.RunMessage(r"""SYNC_ON (turn on P value for synchronization)""", True)
    r.MoveL(p([14983.617710,9407.036110,2000.238100,-131.677829,0.000000,180.000000]),[589.065187,564.889530,776.723289,-175.508863,69.667318,16073.273354,785.000000],[0.0,0.0,0.0])    
    r.MoveL(p([14094.254530,9008.224820,1949.001350,-128.741373,-0.000000,-180.000000]),[582.168271,563.714559,790.102605,-174.999397,70.880202,15144.654140,785.000000],[0.0,0.0,0.0])
    r.RunMessage(r"""SYNC_OFF (turn OFF P value for synchronization)""", True)
    r.MoveL(p([14002.201900,8965.177340,1943.620530,-128.243939,-0.000000,-180.000000]),[580.787514,564.019797,792.173678,-174.877213,70.998343,15050.959494,785.000000],[0.0,0.0,0.0])
    r.MoveL(p([13980.544500,8946.951820,1943.285240,-127.376248,-0.000000,-180.000000]),[575.813761,563.399786,793.167179,-174.571882,71.031480,15036.893522,785.000000],[0.0,0.0,0.0])
    r.RunMessage(r"""SYNC_ON (turn ON P value for synchronization)""", True)
    r.MoveL(p([13972.551650,8940.799700,1943.090850,-126.813295,-0.000000,-180.000000]),[575.804045,566.030408,797.158977,-174.382803,71.071668,15037.122646,785.000000],[0.0,0.0,0.0])
    r.MoveL(p([13500.266850,8731.215100,1913.341960,-126.049528,-0.000000,-180.000000]),[572.831874,563.259297,799.310183,-174.388554,71.581388,14530.665518,785.000000],[0.0,0.0,0.0])
    r.RunMessage(r"""SYNC_OFF (turn OFF P value for synchronization)""", True)
    r.MoveL(p([13496.532790,8729.816160,1913.074600,-126.054323,-0.000000,-180.000000]),[573.016730,563.378439,799.467466,-174.393090,71.586155,14526.674902,785.000000],[0.0,0.0,0.0])
    r.RunMessage(r"""End of generation of : Curve Following.2""", True)
    r.ProgFinish(r"""Curve_Follow_for_PKM2""")
    #r.ProgFinish("Program")
    # robot.ProgSave(".","Program",True)
    for line in r.PROG:
        print(line)
        
    if len(r.LOG) > 0:
        mbox('Program generation LOG:\n\n' + r.LOG)

    input("Press Enter to close...")

if __name__ == "__main__":
    """Function to call when the module is executed by itself: test"""
    test_post()