#!/usr/bin/env node
/**
 * gcode2krl.js — Klipper G-code to KUKA KRL (Robot Language) Converter
 *
 * Converts OrcaSlicer-generated Klipper G-code into KRL .SRC format
 * for KUKA robot arm 3D printing using the PNT_* function library.
 *
 * Usage:
 *   node gcode2krl.js <gcode_file>
 *
 * The script modifies the file IN-PLACE (required by OrcaSlicer post-processor).
 * Rename to .SRC before loading onto KUKA controller.
 */

"use strict";

const fs = require('fs');
const path = require('path');
const os = require('os');

// ============================================================
//  Configuration
// ============================================================

const CONFIG = {
    toolNum: parseInt(process.env.KUKA_TOOL_NUM || '1', 10),
    baseNum: parseInt(process.env.KUKA_BASE_NUM || '1', 10),
    offsetX: parseFloat(process.env.KUKA_COORD_OFFSET_X || '0'),
    offsetY: parseFloat(process.env.KUKA_COORD_OFFSET_Y || '0'),
    offsetZ: parseFloat(process.env.KUKA_COORD_OFFSET_Z || '0'),
    scaleX: parseFloat(process.env.KUKA_COORD_SCALE_X || '1'),
    scaleY: parseFloat(process.env.KUKA_COORD_SCALE_Y || '1'),
    scaleZ: parseFloat(process.env.KUKA_COORD_SCALE_Z || '1'),
    defaultNozzleTemp: 210,
    extruderZone: 1,
    bedZone: 0, // 0 = skip
};

// ============================================================
//  G-code State
// ============================================================

class GCodeState {
    constructor() {
        this.x = 0;
        this.y = 0;
        this.z = 0;
        this.e = 0;
        this.f = 0;
        this.absExtrude = true;   // M82
        this.absPosition = true;  // G90
        this.fanSpeed = 0;
        this.nozzleTemp = null;
    }
}

// ============================================================
//  G-code Parser
// ============================================================

function parseLine(line) {
    let comment = '';
    const semiIdx = line.indexOf(';');
    if (semiIdx >= 0) {
        comment = line.substring(semiIdx + 1).trim();
        line = line.substring(0, semiIdx);
    }
    line = line.trim();
    if (!line) return { cmd: null, params: {}, comment };

    // Klipper macros
    if (line.startsWith('SET_HEATER_TEMPERATURE')) {
        const params = {};
        const m = line.match(/HEATER\s*=\s*(\S+)/);
        if (m) params.HEATER = m[1];
        const t = line.match(/TARGET\s*=\s*(\S+)/);
        if (t) params.TARGET = t[1];
        return { cmd: 'SET_HEATER_TEMPERATURE', params, comment };
    }
    if (line.startsWith('SET_VELOCITY_LIMIT')) {
        const params = {};
        const a = line.match(/ACCEL\s*=\s*(\S+)/);
        if (a) params.ACCEL = a[1];
        const atd = line.match(/ACCEL_TO_DECEL\s*=\s*(\S+)/);
        if (atd) params.ACCEL_TO_DECEL = atd[1];
        return { cmd: 'SET_VELOCITY_LIMIT', params, comment };
    }
    if (line.startsWith('SET_PRESSURE_ADVANCE')) {
        const params = {};
        const a = line.match(/ADVANCE\s*=\s*(\S+)/);
        if (a) params.ADVANCE = a[1];
        return { cmd: 'SET_PRESSURE_ADVANCE', params, comment };
    }

    // Standard G/M-code
    const cmdMatch = line.match(/^(G\d+|M\d+|T\d+)/);
    const cmd = cmdMatch ? cmdMatch[1] : null;
    
    const params = {};
    const paramRe = /([A-Z])\s*([+-]?\d*\.?\d+)/g;
    let m;
    while ((m = paramRe.exec(line)) !== null) {
        params[m[1]] = parseFloat(m[2]);
    }
    
    return { cmd, params, comment };
}

// ============================================================
//  KRL Generator
// ============================================================

class KRLGenerator {
    constructor(jobName) {
        this.jobName = jobName;
        this.lines = [];
        this.state = new GCodeState();
        this.prevZ = 0;
        this.layerCount = 0;
    }

    transformXYZ(x, y, z) {
        return {
            x: x * CONFIG.scaleX + CONFIG.offsetX,
            y: y * CONFIG.scaleY + CONFIG.offsetY,
            z: z * CONFIG.scaleZ + CONFIG.offsetZ,
        };
    }

    fToVel(f) { return Math.round(f / 60.0 * 100) / 100; }

    emit(text, indent = 1) {
        const prefix = '  '.repeat(indent);
        this.lines.push(prefix + text);
    }

    comment(text, indent = 1) {
        this.emit(`; ${text}`, indent);
    }

    updatePosition(params) {
        if (this.state.absPosition) {
            if ('X' in params) this.state.x = params.X;
            if ('Y' in params) this.state.y = params.Y;
            if ('Z' in params) this.state.z = params.Z;
        } else {
            if ('X' in params) this.state.x += params.X;
            if ('Y' in params) this.state.y += params.Y;
            if ('Z' in params) this.state.z += params.Z;
        }
        if ('F' in params) this.state.f = params.F;
    }

    updateE(params) {
        if (!('E' in params)) return { e: this.state.e, extruding: false };
        if (this.state.absExtrude) {
            this.state.e = params.E;
        } else {
            this.state.e += params.E;
        }
        return { e: this.state.e, extruding: true };
    }

    buildPNT_LIN(x, y, z, eVal, vel) {
        const t = this.transformXYZ(x, y, z);
        return `PNT_LIN(${t.x.toFixed(3)}, ${t.y.toFixed(3)}, ${t.z.toFixed(3)}, 0, 0, 0, ${eVal.toFixed(4)}, ${vel.toFixed(1)})`;
    }

    buildPNT_G0(x, y, z, vel) {
        const t = this.transformXYZ(x, y, z);
        return `PNT_G0(${t.x.toFixed(3)}, ${t.y.toFixed(3)}, ${t.z.toFixed(3)}, 0, 0, 0, ${vel.toFixed(1)})`;
    }

    emitHeader() {
        this.lines.push('&ACCESS RVP');
        this.lines.push('&REL 1');
        this.lines.push('&COMMENT Generated by gcode2krl from OrcaSlicer');
        this.lines.push('&PARAM PUBLIC');
        this.lines.push('');
        this.lines.push(`DEF ${this.jobName}()`);
        this.lines.push('');
        this.comment('--- External Function Declarations ---');
        const exts = [
            'PNT_INIT()', 'PNT_FINISH()', 'PNT_G92_E0()',
            'PNT_RETRACT()', 'PNT_UNRETRACT()',
            'PNT_LIN(REAL :IN,REAL :IN,REAL :IN,REAL :IN,REAL :IN,REAL :IN,REAL :IN,REAL :IN)',
            'PNT_G0(REAL :IN,REAL :IN,REAL :IN,REAL :IN,REAL :IN,REAL :IN,REAL :IN)',
            'PNT_HEAT_ON(INT :IN,INT :IN)', 'PNT_HEAT_OFF(INT :IN)',
            'PNT_HEAT_ALL(INT :IN,INT :IN,INT :IN,INT :IN,INT :IN,INT :IN)',
            'PNT_HEAT_WAIT_ALL(INT :IN)',
            'PNT_FAN_ON()', 'PNT_FAN_OFF()', 'PNT_FAN_SET(INT :IN)',
            'PNT_DWELL(INT :IN)', 'PNT_LOG(CHAR[] :IN)',
            'PNT_SET_PRINT_SPEED(REAL :IN)', 'PNT_SET_TRAVEL_SPEED(REAL :IN)',
        ];
        exts.forEach(fn => this.emit(`EXT ${fn}`));
        this.lines.push('');
        this.comment('--- Tool & Base Configuration ---');
        this.emit(`$TOOL = TOOL_DATA[${CONFIG.toolNum}]`);
        this.emit(`$BASE = BASE_DATA[${CONFIG.baseNum}]`);
        this.lines.push('');
    }

    emitInit() {
        this.comment('==================================================');
        this.comment('  Initialization');
        this.comment('==================================================');
        this.emit('PNT_INIT()');
        this.lines.push('');
    }

    emitHeating(temp) {
        if (temp && temp > 0) {
            this.comment('--- Preheating ---');
            this.emit(`PNT_HEAT_ON(${CONFIG.extruderZone}, ${temp})`);
            this.emit('PNT_HEAT_WAIT_ALL(180000)  ; 3 minute timeout');
            this.lines.push('');
            this.comment('--- Pre-print Prep ---');
            this.emit('PNT_G92_E0()');
            this.emit('PNT_UNRETRACT()');
            this.emit('PNT_FAN_ON()');
            this.lines.push('');
        }
    }

    emitFooter() {
        this.lines.push('');
        this.comment('--- Print Complete ---');
        this.emit('PNT_RETRACT()');
        this.emit('PNT_FAN_OFF()');
        this.emit('PNT_HEAT_ALL(0, 0, 0, 0, 0, 0)');
        this.emit('PNT_FINISH()');
        this.lines.push('');
        this.lines.push('END');
    }

    emitLayerChange() {
        this.lines.push('');
        this.comment('--- Layer Change ---');
        this.emit('PNT_G92_E0()');
        this.emit('PNT_RETRACT()');
    }

    handleG0G1(cmd, params, comment, isLayerChange = false) {
        const hasE = 'E' in params;
        const hasXY = 'X' in params || 'Y' in params;
        const hasZ = 'Z' in params;

        const eResult = this.updateE(params);
        const extruding = eResult.extruding;

        let targetX = this.state.x, targetY = this.state.y, targetZ = this.state.z;
        if (this.state.absPosition) {
            if ('X' in params) targetX = params.X;
            if ('Y' in params) targetY = params.Y;
            if ('Z' in params) targetZ = params.Z;
        } else {
            if ('X' in params) targetX += params.X;
            if ('Y' in params) targetY += params.Y;
            if ('Z' in params) targetZ += params.Z;
        }

        let feedrate = params.F || this.state.f || 3600;
        const vel = this.fToVel(feedrate);

        if (comment && comment.trim() && !comment.startsWith('LAYER:')) {
            this.comment(comment.trim());
        }

        // Layer change: Z lift only
        if (isLayerChange && hasZ) {
            this.emit(this.buildPNT_G0(targetX, targetY, targetZ, vel));
            this.updatePosition(params);
            return;
        }

        // Travel (no extrusion)
        if (!extruding) {
            if (hasXY || hasZ) {
                this.emit(this.buildPNT_G0(targetX, targetY, targetZ, vel));
            }
            this.updatePosition(params);
            return;
        }

        // Extrusion move
        if (hasXY || hasZ) {
            this.emit(this.buildPNT_LIN(targetX, targetY, targetZ, this.state.e, vel));
        }
        this.updatePosition(params);
    }

    handleG92(params) {
        if (params.E === 0) {
            this.state.e = 0;
            this.emit('PNT_G92_E0()');
        }
        if ('X' in params) this.state.x = params.X;
        if ('Y' in params) this.state.y = params.Y;
        if ('Z' in params) this.state.z = params.Z;
    }

    skipCmd(msg) {
        this.comment(`Skipped: ${msg}`);
    }
}

// ============================================================
//  Layer change detection
// ============================================================

function isLayerChange(comment) {
    if (!comment) return false;
    const c = comment.toUpperCase();
    return ['LAYER_CHANGE', 'BEFORE_LAYER_CHANGE', '[LAYER_Z]',
            'AFTER_LAYER_CHANGE'].some(kw => c.includes(kw));
}

// ============================================================
//  Main Conversion
// ============================================================

function convertGcodeToKRL(gcodePath) {
    const content = fs.readFileSync(gcodePath, 'utf-8');
    const lines = content.split(/\r?\n/);

    // Extract job name
    let jobName = path.basename(gcodePath, path.extname(gcodePath));
    jobName = jobName.replace(/[^A-Za-z0-9_]/g, '_');
    if (!jobName || /^\d/.test(jobName)) jobName = 'P_' + jobName;

    const gen = new KRLGenerator(jobName);

    // Phase 1: find max temperature
    let nozzleTemp = null;
    for (const line of lines) {
        const { cmd, params } = parseLine(line);
        if ((cmd === 'M104' || cmd === 'M109') && 'S' in params) {
            const t = Math.round(params.S);
            if (nozzleTemp === null || t > nozzleTemp) nozzleTemp = t;
        }
        if (cmd === 'SET_HEATER_TEMPERATURE' && params.HEATER === 'extruder') {
            const t = Math.round(parseFloat(params.TARGET || 0));
            if (nozzleTemp === null || t > nozzleTemp) nozzleTemp = t;
        }
    }
    if (nozzleTemp === null) nozzleTemp = CONFIG.defaultNozzleTemp;

    // Phase 2: generate KRL
    gen.emitHeader();
    gen.emitInit();
    gen.emitHeating(nozzleTemp);
    gen.emit('PNT_SET_PRINT_SPEED(60.0)');
    gen.emit('PNT_SET_TRAVEL_SPEED(300.0)');
    gen.lines.push('');

    let pendingLayerChange = false;
    let inBody = false;

    for (const line of lines) {
        const { cmd, params, comment } = parseLine(line);

        if (!inBody) {
            if (cmd || Object.keys(params).length > 0) {
                inBody = true;
            } else if (line.trim() && !line.trim().startsWith(';')) {
                inBody = true;
            } else {
                continue;
            }
        }

        // Layer change
        if (isLayerChange(comment)) {
            pendingLayerChange = true;
            gen.comment(comment);
            continue;
        }

        if (!cmd) {
            if (comment && !isLayerChange(comment)) gen.comment(comment);
            continue;
        }

        // Motion
        if (cmd === 'G0' || cmd === 'G1') {
            if (pendingLayerChange) {
                gen.emitLayerChange();
                pendingLayerChange = false;
                
                if ('Z' in params) {
                    const newZ = gen.state.absPosition ? params.Z : gen.state.z + params.Z;
                    if (newZ > gen.prevZ + 0.01) {
                        gen.prevZ = newZ;
                        gen.layerCount++;
                        gen.comment(`Layer ${gen.layerCount}, Z=${newZ.toFixed(3)}`);
                        gen.emit('PNT_UNRETRACT()');
                    }
                }
                gen.handleG0G1(cmd, params, comment, false);
            } else {
                gen.handleG0G1(cmd, params, comment);
            }
            continue;
        }

        switch (cmd) {
            case 'G4': gen.emit(`PNT_DWELL(${Math.round(params.P || 0)})`); break;
            case 'G21': break; // mm units
            case 'G20': gen.skipCmd('G20 inches — use G21 mm'); break;
            case 'G28': gen.skipCmd('G28 homing (handled by PNT_INIT)'); break;
            case 'G90': gen.state.absPosition = true; break;
            case 'G91': gen.state.absPosition = false; break;
            case 'G92': gen.handleG92(params); break;
            case 'M82': gen.state.absExtrude = true; break;
            case 'M83': gen.state.absExtrude = false; break;
            case 'M104': case 'M109':
                // Temperature already set in header phase, skip redundant calls
                break;
            case 'M140': case 'M190':
                gen.skipCmd(`${cmd} bed temp (no heated bed)`);
                break;
            case 'M106':
                const s = Math.round(params.S !== undefined ? params.S : 255);
                gen.state.fanSpeed = s;
                if (s === 0) {
                    gen.emit('PNT_FAN_OFF()');
                } else if (s >= 255) {
                    gen.emit('PNT_FAN_ON()');
                } else {
                    gen.emit(`PNT_FAN_SET(${s})`);
                }
                break;
            case 'M107':
                gen.emit('PNT_FAN_OFF()');
                gen.state.fanSpeed = 0;
                break;
            case 'SET_HEATER_TEMPERATURE':
                // Already handled in header phase
                break;
            case 'SET_VELOCITY_LIMIT':
                gen.skipCmd('SET_VELOCITY_LIMIT (KRL uses BAS(#VEL_CP))');
                break;
            case 'SET_PRESSURE_ADVANCE':
                gen.skipCmd('SET_PRESSURE_ADVANCE');
                break;
            // Default: ignore unknown commands
        }
    }

    gen.emitFooter();
    return gen.lines.join('\n') + '\n';
}

// ============================================================
//  CLI
// ============================================================

function main() {
    const args = process.argv.slice(2);
    if (args.length < 1) {
        console.error('Usage: node gcode2krl.js <gcode_file>');
        console.error('');
        console.error('Converts OrcaSlicer Klipper G-code to KUKA KRL (.SRC)');
        console.error('The file is modified IN-PLACE (required by OrcaSlicer post-processor)');
        console.error('');
        console.error('Environment variables:');
        console.error('  KUKA_TOOL_NUM         Tool number (default: 1)');
        console.error('  KUKA_BASE_NUM         Base number (default: 1)');
        console.error('  KUKA_COORD_OFFSET_X   X offset (default: 0)');
        console.error('  KUKA_COORD_OFFSET_Y   Y offset (default: 0)');
        console.error('  KUKA_COORD_OFFSET_Z   Z offset (default: 0)');
        process.exit(1);
    }

    const gcodePath = args[0];
    if (!fs.existsSync(gcodePath)) {
        console.error(`ERROR: File not found: ${gcodePath}`);
        process.exit(1);
    }

    console.error(`gcode2krl: Converting ${gcodePath}...`);

    try {
        const krlContent = convertGcodeToKRL(gcodePath);
        fs.writeFileSync(gcodePath, krlContent, 'utf-8');
        const lineCount = krlContent.split('\n').length;
        console.error(`gcode2krl: Done. ${lineCount} lines written to ${gcodePath}`);
        console.error('Rename to .SRC before loading to KUKA controller.');
    } catch (e) {
        console.error(`ERROR: Conversion failed: ${e.message}`);
        console.error(e.stack);
        process.exit(1);
    }
}

main();
