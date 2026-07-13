#!/usr/bin/env python3
"""
gcode2krl.py — Klipper G-code to KUKA KRL (Robot Language) Converter

Converts OrcaSlicer-generated Klipper G-code into KRL .SRC format
for KUKA robot arm 3D printing using the PNT_* function library.

Usage:
    gcode2krl.py <gcode_file>
    
The script modifies the file IN-PLACE (required by OrcaSlicer post-processor).
Rename to .SRC before loading onto KUKA controller.

KRL Function Dependencies:
    PNT_INIT, PNT_FINISH, PNT_LIN, PNT_G0, PNT_G92_E0,
    PNT_RETRACT, PNT_UNRETRACT, PNT_HEAT_ON, PNT_HEAT_ALL,
    PNT_HEAT_WAIT_ALL, PNT_FAN_ON, PNT_FAN_OFF, PNT_FAN_SET,
    PNT_DWELL, PNT_SET_PRINT_SPEED, PNT_SET_TRAVEL_SPEED
"""

import re
import sys
import os
import math
from typing import Optional, List, Tuple


# ============================================================
#  Configuration
# ============================================================

# KUKA system defaults (override via environment variables)
DEFAULT_TOOL = int(os.environ.get("KUKA_TOOL_NUM", "1"))
DEFAULT_BASE = int(os.environ.get("KUKA_BASE_NUM", "1"))
DEFAULT_E_DEG_PER_MM = float(os.environ.get("KUKA_E_DEG_PER_MM", "15.3"))

# Default temperature if not specified in G-code
DEFAULT_NOZZLE_TEMP = 210
DEFAULT_BED_TEMP = 60

# Heating zone mapping
EXTRUDER_HEATER_ZONE = 1
BED_HEATER_ZONE = 0  # 0 means skip (no heated bed)

# Comment prefix
KRL_COMMENT = ";"

# Motion parameter mapping
# G-code F (mm/min) → KRL vel (mm/s)
def f_to_vel(f_mm_per_min: float) -> float:
    return round(f_mm_per_min / 60.0, 2)

# G-code X/Y/Z → KUKA coordinates
# Default: no transformation (identity)
# Override by setting KUKA_COORD_{OFFSET,ROTATE}_* env vars
COORD_OFFSET_X = float(os.environ.get("KUKA_COORD_OFFSET_X", "0"))
COORD_OFFSET_Y = float(os.environ.get("KUKA_COORD_OFFSET_Y", "0"))
COORD_OFFSET_Z = float(os.environ.get("KUKA_COORD_OFFSET_Z", "0"))
COORD_SCALE_X  = float(os.environ.get("KUKA_COORD_SCALE_X", "1"))
COORD_SCALE_Y  = float(os.environ.get("KUKA_COORD_SCALE_Y", "1"))
COORD_SCALE_Z  = float(os.environ.get("KUKA_COORD_SCALE_Z", "1"))


# ============================================================
#  G-code Parser State
# ============================================================

class GCodeState:
    """Tracks G-code interpreter state for correct KRL conversion."""
    
    def __init__(self):
        self.x: float = 0.0
        self.y: float = 0.0
        self.z: float = 0.0
        self.e: float = 0.0          # Accumulated E (absolute mode)
        self.e_offset: float = 0.0   # G92 offset
        self.f: float = 0.0          # Current feedrate (mm/min)
        self.abs_extrude: bool = True  # M82 (True) or M83 (False)
        self.abs_position: bool = True # G90 (True) or G91 (False)
        self.fan_speed: int = 0
        self.nozzle_temp: Optional[int] = None
        self.line_number: int = 0
        self.last_extrude_e: float = 0.0  # E at last extrusion (for relative calc)
        self.is_extruding: bool = False


# ============================================================
#  G-code Tokenizer
# ============================================================

GCODE_LINE_RE = re.compile(
    r'^\s*'                          # leading whitespace
    r'(?:N\d+\s+)?'                  # optional line number Nxxx
    r'(?:'                           # optional command
        r'(?P<cmd>G\d+|M\d+|T\d+)'   # G-code / M-code / T-code
        r'|SET_HEATER_TEMPERATURE'   # Klipper macro
        r'|SET_VELOCITY_LIMIT'       # Klipper macro
        r'|SET_PRESSURE_ADVANCE'     # Klipper macro
    r')?'
    r'(?P<rest>.*)'                  # rest of line
)

PARAM_RE = re.compile(r'([A-Z])\s*([+-]?\d*\.?\d+)')


def parse_line(line: str) -> Tuple[Optional[str], dict, str]:
    """Parse a single G-code line.
    Returns: (command, params_dict, comment_string)
    params_dict keys are uppercase letters (X, Y, Z, E, F, S, P, T, etc.)
    """
    comment = ""
    if ';' in line:
        line, comment = line.split(';', 1)
    
    # Check for Klipper-style macros
    if line.strip().startswith('SET_HEATER_TEMPERATURE'):
        cmd = 'SET_HEATER_TEMPERATURE'
        rest = line.strip()[len(cmd):]
        params = {}
        for match in re.finditer(r'(HEATER|TARGET)\s*=\s*(\S+)', rest):
            params[match.group(1)] = match.group(2)
        return cmd, params, comment.strip()
    
    if line.strip().startswith('SET_VELOCITY_LIMIT'):
        cmd = 'SET_VELOCITY_LIMIT'
        rest = line.strip()[len(cmd):]
        params = {}
        for match in re.finditer(r'(ACCEL|ACCEL_TO_DECEL|SQUARE_CORNER_VELOCITY)\s*=\s*(\S+)', rest):
            params[match.group(1)] = float(match.group(2))
        return cmd, params, comment.strip()
    
    if line.strip().startswith('SET_PRESSURE_ADVANCE'):
        cmd = 'SET_PRESSURE_ADVANCE'
        rest = line.strip()[len(cmd):]
        params = {}
        for match in re.finditer(r'(ADVANCE|SMOOTH_TIME)\s*=\s*(\S+)', rest):
            params[match.group(1)] = float(match.group(2))
        return cmd, params, comment.strip()
    
    # Standard G/M-code
    m = GCODE_LINE_RE.match(line)
    if not m:
        return None, {}, comment.strip()
    
    cmd = m.group('cmd') or None
    rest = m.group('rest') or ''
    
    params = {}
    for key, val in PARAM_RE.findall(rest):
        try:
            params[key] = float(val)
        except ValueError:
            pass
    
    return cmd, params, comment.strip()


# ============================================================
#  KRL Code Generator
# ============================================================

class KRLGenerator:
    """Generates KRL .SRC output from parsed G-code state."""
    
    def __init__(self, job_name: str = "ROBOT3D"):
        self.job_name = job_name
        self.lines: List[str] = []
        self.state = GCodeState()
        self.heater_enabled = [False] * 7  # index 1-6
        self.heater_temp = [0] * 7
        self.layer_change_pending = False
        self.first_extrude = True
        self._z_current = 0.0
        self._e_relative_base = 0.0  # for M83 relative mode tracking
        self._in_layer = False
    
    def _transform_xyz(self, x: float, y: float, z: float) -> Tuple[float, float, float]:
        """Apply coordinate transformation from G-code to KUKA base frame."""
        return (
            x * COORD_SCALE_X + COORD_OFFSET_X,
            y * COORD_SCALE_Y + COORD_OFFSET_Y,
            z * COORD_SCALE_Z + COORD_OFFSET_Z,
        )
    
    def _emit(self, text: str, indent: int = 1):
        """Add a line to the output."""
        prefix = "  " * indent
        self.lines.append(f"{prefix}{text}")
    
    def _comment(self, text: str, indent: int = 1):
        """Add a comment line."""
        self._emit(f"{KRL_COMMENT} {text}", indent)
    
    def _update_position(self, params: dict):
        """Update state from movement parameters."""
        if self.state.abs_position:
            if 'X' in params: self.state.x = params['X']
            if 'Y' in params: self.state.y = params['Y']
            if 'Z' in params: self.state.z = params['Z']
        else:
            if 'X' in params: self.state.x += params['X']
            if 'Y' in params: self.state.y += params['Y']
            if 'Z' in params: self.state.z += params['Z']
        if 'F' in params:
            self.state.f = params['F']
    
    def _update_e(self, params: dict):
        """Update extruder state - returns (absolute_e_value, is_extruding)"""
        if 'E' not in params:
            return self.state.e, False
        
        e_raw = params['E']
        if self.state.abs_extrude:
            # M82: absolute E
            self.state.e = e_raw
        else:
            # M83: relative E
            self.state.e += e_raw
        
        return self.state.e, True
    
    def _build_pnt_lin(self, x: float, y: float, z: float, e_val: float, vel: float) -> str:
        """Build a PNT_LIN call string."""
        kx, ky, kz = self._transform_xyz(x, y, z)
        return (f"PNT_LIN({kx:.3f}, {ky:.3f}, {kz:.3f}, "
                f"0, 0, 0, {e_val:.4f}, {vel:.1f})")
    
    def _build_pnt_g0(self, x: float, y: float, z: float, vel: float) -> str:
        """Build a PNT_G0 (travel) call string."""
        kx, ky, kz = self._transform_xyz(x, y, z)
        return f"PNT_G0({kx:.3f}, {ky:.3f}, {kz:.3f}, 0, 0, 0, {vel:.1f})"
    
    # ---- KRL file structure ----
    
    def emit_header(self):
        """Emit the KRL file header with DEF and EXT declarations."""
        self.lines.append("&ACCESS RVP")
        self.lines.append("&REL 1")
        self.lines.append(f"&COMMENT Generated by gcode2krl from OrcaSlicer")
        self.lines.append(f"&PARAM PUBLIC")
        self.lines.append("")
        self.lines.append(f"DEF {self.job_name}()")
        self.lines.append("")
        self._comment("--- External Function Declarations ---")
        ext_funcs = [
            "PNT_INIT()",
            "PNT_FINISH()",
            "PNT_G92_E0()",
            "PNT_RETRACT()",
            "PNT_UNRETRACT()",
            "PNT_LIN(REAL :IN, REAL :IN, REAL :IN, REAL :IN, REAL :IN, REAL :IN, REAL :IN, REAL :IN)",
            "PNT_G0(REAL :IN, REAL :IN, REAL :IN, REAL :IN, REAL :IN, REAL :IN, REAL :IN)",
            "PNT_HEAT_ON(INT :IN, INT :IN)",
            "PNT_HEAT_OFF(INT :IN)",
            "PNT_HEAT_ALL(INT :IN, INT :IN, INT :IN, INT :IN, INT :IN, INT :IN)",
            "PNT_HEAT_WAIT_ALL(INT :IN)",
            "PNT_FAN_ON()",
            "PNT_FAN_OFF()",
            "PNT_FAN_SET(INT :IN)",
            "PNT_DWELL(INT :IN)",
            "PNT_LOG(CHAR[] :IN)",
            "PNT_SET_PRINT_SPEED(REAL :IN)",
            "PNT_SET_TRAVEL_SPEED(REAL :IN)",
        ]
        for fn in ext_funcs:
            self._emit(f"EXT {fn}")
        self.lines.append("")
        self._comment("--- Tool & Base Configuration ---")
        self._emit(f"$TOOL = TOOL_DATA[{DEFAULT_TOOL}]")
        self._emit(f"$BASE = BASE_DATA[{DEFAULT_BASE}]")
        self.lines.append("")
    
    def emit_init(self):
        """Emit the initialization section."""
        self._comment("=" * 50)
        self._comment("Initialization")
        self._comment("=" * 50)
        self._emit("PNT_INIT()")
        self.lines.append("")
    
    def emit_heating(self, nozzle_temp: int):
        """Emit heating commands if temperature was set in G-code."""
        if nozzle_temp and nozzle_temp > 0:
            self._comment("--- Preheating ---")
            self._emit(f"PNT_HEAT_ON({EXTRUDER_HEATER_ZONE}, {nozzle_temp})")
            self._emit(f"PNT_HEAT_WAIT_ALL(180000)  ; 3 minute timeout")
            self._comment(f"Target: {nozzle_temp}C")
            self.lines.append("")
            self._comment("--- Pre-print Prep ---")
            self._emit("PNT_G92_E0()")
            self._emit("PNT_UNRETRACT()")
            self._emit("PNT_FAN_ON()")
            self.lines.append("")
    
    def emit_footer(self):
        """Emit the end-of-file footer."""
        self.lines.append("")
        self._comment("--- Print Complete ---")
        self._emit("PNT_RETRACT()")
        self._emit("PNT_FAN_OFF()")
        self._emit("PNT_HEAT_ALL(0, 0, 0, 0, 0, 0)")
        self._emit("PNT_FINISH()")
        self.lines.append("")
        self.lines.append("END")
    
    def emit_layer_change(self):
        """Emit layer change sequence."""
        self.lines.append("")
        self._comment("--- Layer Change ---")
        self._emit("PNT_G92_E0()")
        self._emit("PNT_RETRACT()")
    
    def emit_unretract(self):
        """Unretract after travel to new layer."""
        self._emit("PNT_UNRETRACT()")
    
    # ---- Command handlers ----
    
    def handle_g0_g1(self, cmd: str, params: dict, comment: str, is_layer_change: bool = False):
        """Handle G0 (travel) or G1 (extrude/travel) commands."""
        # Determine if this is extrusion or travel
        has_e = 'E' in params
        has_xy = 'X' in params or 'Y' in params
        has_z = 'Z' in params
        
        # Update E state before position
        if has_e:
            self.state.e, is_extruding = self._update_e(params)
        else:
            is_extruding = False
        
        # Get target coordinates
        target_x = params.get('X', self.state.x) if self.state.abs_position else self.state.x + params.get('X', 0)
        target_y = params.get('Y', self.state.y) if self.state.abs_position else self.state.y + params.get('Y', 0)
        target_z = self.state.z
        if 'Z' in params:
            target_z = params['Z'] if self.state.abs_position else self.state.z + params['Z']
        
        # Feedrate
        feedrate = params.get('F', self.state.f)
        if feedrate == 0:
            feedrate = 3600  # default 60mm/s
        vel = f_to_vel(feedrate)
        
        # Emit comment if meaningful
        if comment and comment.strip() and not comment.startswith('LAYER:'):
            self._comment(comment.strip())
        
        # Layer change: handle Z move as separate travel
        if is_layer_change and has_z:
            # Emit Z lift as travel
            self._emit(self._build_pnt_g0(target_x, target_y, target_z, vel))
            self._update_position(params)
            return
        
        # Pure Z move or XY+Z move without E
        if not has_e:
            if has_xy or has_z:
                self._emit(self._build_pnt_g0(target_x, target_y, target_z, vel))
            self._update_position(params)
            return
        
        # Extrusion move
        if has_xy or has_z:
            e_abs = self.state.e
            self._emit(self._build_pnt_lin(target_x, target_y, target_z, e_abs, vel))
        
        self._update_position(params)
    
    def handle_g92(self, params: dict):
        """Handle G92 (set position). For E0, emit PNT_G92_E0()."""
        if 'E' in params and params['E'] == 0:
            self.state.e_offset = self.state.e
            self.state.e = 0
            self._emit("PNT_G92_E0()")
        # Also handle XYZ if needed
        if 'X' in params: self.state.x = params['X']
        if 'Y' in params: self.state.y = params['Y']
        if 'Z' in params: self.state.z = params['Z']
    
    def handle_m104_m109(self, cmd: str, params: dict):
        """Handle M104 (set temp) / M109 (set temp & wait)."""
        if 'S' not in params:
            return
        temp = int(params['S'])
        zone = EXTRUDER_HEATER_ZONE
        # Map T parameter to heater zone
        if 'T' in params:
            try:
                zone = int(params['T']) + 1  # T0 → zone 1
            except ValueError:
                pass
        
        self.heater_enabled[zone] = True
        self.heater_temp[zone] = temp
        self.state.nozzle_temp = temp
        
        if cmd == 'M109' and temp > 0:
            self._emit(f"PNT_HEAT_ON({zone}, {temp})")
            self._emit(f"PNT_HEAT_WAIT_ALL(180000)")
        elif temp > 0:
            self._emit(f"PNT_HEAT_ON({zone}, {temp})")
    
    def handle_m140_m190(self, cmd: str, params: dict):
        """Handle M140/M190 (bed temp) — skip, no heated bed."""
        if 'S' in params:
            temp = int(params['S'])
            if BED_HEATER_ZONE > 0:
                if cmd == 'M190':
                    self._emit(f"PNT_HEAT_WAIT_ALL(300000)")
            else:
                self._comment(f"Skipped M140/M190 (no heated bed): S={temp}")
    
    def handle_m106(self, params: dict):
        """Handle M106 (fan on/speed)."""
        speed = int(params.get('S', 255))
        if speed > 0:
            if speed >= 255:
                self._emit("PNT_FAN_ON()")
            else:
                self._emit(f"PNT_FAN_SET({speed})")
        else:
            self._emit("PNT_FAN_OFF()")
        self.state.fan_speed = speed
    
    def handle_m107(self):
        """Handle M107 (fan off)."""
        self._emit("PNT_FAN_OFF()")
        self.state.fan_speed = 0
    
    def handle_g4(self, params: dict):
        """Handle G4 (dwell)."""
        ms = int(params.get('P', 0))
        if ms > 0:
            self._emit(f"PNT_DWELL({ms})")
    
    def handle_klipper_temp(self, params: dict, comment: str):
        """Handle SET_HEATER_TEMPERATURE Klipper macro."""
        heater = params.get('HEATER', '')
        target = params.get('TARGET', '0')
        try:
            temp = int(float(target))
        except ValueError:
            return
        
        if heater == 'extruder' or heater.startswith('extruder'):
            zone = EXTRUDER_HEATER_ZONE
            self.state.nozzle_temp = temp
            if temp > 0:
                self._emit(f"PNT_HEAT_ON({zone}, {temp})")
                self._comment(f"Klipper: {heater} → {temp}C")
        elif heater == 'heater_bed':
            self._comment(f"Skipped bed temp: {temp}C (no heated bed)")
    
    def handle_klipper_velocity(self, params: dict):
        """Handle SET_VELOCITY_LIMIT — skip, KRL uses $VEL_CP."""
        accel = params.get('ACCEL', 'N/A')
        self._comment(f"SET_VELOCITY_LIMIT skipped (KRL uses BAS(#VEL_CP))")
    
    def handle_klipper_pa(self, params: dict):
        """Handle SET_PRESSURE_ADVANCE — optionally emit PNT_SET_PRESSURE_ADVANCE."""
        advance = params.get('ADVANCE', 0)
        if advance > 0:
            self._comment(f"SET_PRESSURE_ADVANCE ignored (not implemented in KRL)")


# ============================================================
#  Layer change detection
# ============================================================

def is_layer_change_line(comment: str) -> bool:
    """Check if a comment indicates a layer change."""
    if not comment:
        return False
    return any(kw in comment.upper() for kw in [
        'LAYER_CHANGE', 'BEFORE_LAYER_CHANGE', '[LAYER_Z]',
        'AFTER_LAYER_CHANGE', 'LAYER:'
    ])


# ============================================================
#  Main Conversion
# ============================================================

def convert_gcode_to_krl(gcode_path: str) -> str:
    """Convert a G-code file to KRL. Returns the KRL content as string."""
    
    # Read G-code
    with open(gcode_path, 'r', encoding='utf-8', errors='replace') as f:
        gcode_lines = f.readlines()
    
    # Extract job name from filename
    job_name = os.path.splitext(os.path.basename(gcode_path))[0]
    # Sanitize: KUKA names must be valid identifiers
    job_name = re.sub(r'[^A-Za-z0-9_]', '_', job_name)
    if not job_name or job_name[0].isdigit():
        job_name = 'P_' + job_name
    
    gen = KRLGenerator(job_name)
    
    # Phase 1: Scan for maximum temperature
    nozzle_temp = None
    for line in gcode_lines:
        cmd, params, comment = parse_line(line)
        if cmd in ('M104', 'M109') and 'S' in params:
            t = int(params['S'])
            if nozzle_temp is None or t > nozzle_temp:
                nozzle_temp = t
        if cmd == 'SET_HEATER_TEMPERATURE' and params.get('HEATER', '') == 'extruder':
            try:
                t = int(float(params.get('TARGET', 0)))
                if nozzle_temp is None or t > nozzle_temp:
                    nozzle_temp = t
            except ValueError:
                pass
    
    # Default temperature
    if nozzle_temp is None:
        nozzle_temp = DEFAULT_NOZZLE_TEMP
    
    # Phase 2: Build KRL output
    gen.emit_header()
    gen.emit_init()
    gen.emit_heating(nozzle_temp)
    
    # Emit initial print speed settings
    gen._emit("PNT_SET_PRINT_SPEED(60.0)")
    gen._emit("PNT_SET_TRAVEL_SPEED(300.0)")
    gen.lines.append("")
    
    # Track state for layer detection
    prev_z = 0.0
    layer_count = 0
    pending_layer_change = False
    
    # Skip lines until we see the first meaningful move (skip header comments)
    in_body = False
    
    for line_num, line in enumerate(gcode_lines, 1):
        cmd, params, comment = parse_line(line)
        
        # Skip empty lines and pure comments at start
        if not in_body:
            if cmd or params:
                in_body = True
            elif line.strip() and not line.strip().startswith(';'):
                in_body = True
            else:
                continue
        
        gen.state.line_number = line_num
        
        # ---- Layer change detection ----
        if is_layer_change_line(comment):
            # Check if Z actually changes in the next G1
            pending_layer_change = True
            gen._comment(comment.strip())
            continue
        
        # ---- Command dispatch ----
        if cmd is None:
            # Pure comment or unrecognized
            if comment.strip():
                # Don't duplicate layer change comments
                if not is_layer_change_line(comment):
                    gen._comment(comment.strip())
            continue
        
        if cmd == 'G0' or cmd == 'G1':
            if pending_layer_change:
                gen.emit_layer_change()
                pending_layer_change = False
                
                # Check if next Z is higher (new layer)
                if 'Z' in params:
                    new_z = params['Z'] if gen.state.abs_position else gen.state.z + params['Z']
                    if new_z > prev_z + 0.01:
                        prev_z = new_z
                        layer_count += 1
                        gen._comment(f"Layer {layer_count}, Z={new_z:.3f}")
                        gen.emit_unretract()
                
                # Handle movement (travel to next position, no extrude)
                gen.handle_g0_g1(cmd, params, comment, is_layer_change=False)
            else:
                # Normal movement
                gen.handle_g0_g1(cmd, params, comment)
        
        elif cmd == 'G4':
            gen.handle_g4(params)
        
        elif cmd == 'G20':
            gen._comment("WARNING: G20 (inches) not supported, use G21 (mm)")
        
        elif cmd == 'G21':
            pass  # mm units - default
        
        elif cmd == 'G28':
            gen._comment("G28 homing skipped (handled by PNT_INIT)")
        
        elif cmd == 'G90':
            gen.state.abs_position = True
        
        elif cmd == 'G91':
            gen.state.abs_position = False
        
        elif cmd == 'G92':
            gen.handle_g92(params)
        
        elif cmd == 'M82':
            gen.state.abs_extrude = True
        
        elif cmd == 'M83':
            gen.state.abs_extrude = False
        
        elif cmd in ('M104', 'M109'):
            pass  # Temperature already set in header phase
        
        elif cmd in ('M140', 'M190'):
            pass  # No heated bed
        
        elif cmd == 'M106':
            gen.handle_m106(params)
        
        elif cmd == 'M107':
            gen.handle_m107()
        
        elif cmd == 'M105':
            pass  # Temperature report - no-op
        
        elif cmd in ('M84', 'M18'):
            pass  # Disable motors - no-op for robot
        
        elif cmd in ('M112', 'M999'):
            gen._comment(f"Emergency stop signal: {cmd}")
        
        elif cmd and cmd.startswith('M'):
            # Unknown M-code
            pass
        
        elif cmd == 'SET_HEATER_TEMPERATURE':
            gen.handle_klipper_temp(params, comment)
        
        elif cmd == 'SET_VELOCITY_LIMIT':
            gen.handle_klipper_velocity(params)
        
        elif cmd == 'SET_PRESSURE_ADVANCE':
            gen.handle_klipper_pa(params)
    
    gen.emit_footer()
    
    return '\n'.join(gen.lines) + '\n'


# ============================================================
#  CLI Entry Point
# ============================================================

def main():
    if len(sys.argv) < 2:
        print("Usage: gcode2krl.py <gcode_file>", file=sys.stderr)
        print("", file=sys.stderr)
        print("Converts OrcaSlicer Klipper G-code to KUKA KRL (.SRC)", file=sys.stderr)
        print("The file is modified IN-PLACE (required by OrcaSlicer post-processor)", file=sys.stderr)
        print("", file=sys.stderr)
        print("Environment variables:", file=sys.stderr)
        print("  KUKA_TOOL_NUM        Tool number (default: 1)", file=sys.stderr)
        print("  KUKA_BASE_NUM        Base number (default: 1)", file=sys.stderr)
        print("  KUKA_COORD_OFFSET_X  X offset from G-code to KUKA (default: 0)", file=sys.stderr)
        print("  KUKA_COORD_OFFSET_Y  Y offset (default: 0)", file=sys.stderr)
        print("  KUKA_COORD_OFFSET_Z  Z offset (default: 0)", file=sys.stderr)
        sys.exit(1)
    
    gcode_path = sys.argv[1]
    
    if not os.path.isfile(gcode_path):
        print(f"ERROR: File not found: {gcode_path}", file=sys.stderr)
        sys.exit(1)
    
    print(f"gcode2krl: Converting {gcode_path}...", file=sys.stderr)
    
    try:
        krl_content = convert_gcode_to_krl(gcode_path)
    except Exception as e:
        print(f"ERROR: Conversion failed: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
    
    # Write in-place (required by OrcaSlicer post-processor)
    with open(gcode_path, 'w', encoding='utf-8') as f:
        f.write(krl_content)
    
    print(f"gcode2krl: Done. Output written to {gcode_path}", file=sys.stderr)
    print(f"  Lines: {len(krl_content.splitlines())}", file=sys.stderr)
    print(f"  Rename to .SRC before loading to KUKA controller.", file=sys.stderr)


if __name__ == '__main__':
    main()
