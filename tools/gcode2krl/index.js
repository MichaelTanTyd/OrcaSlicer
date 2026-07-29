/**
 * gcode2krl v3.0 — OrcaSlicer GCode → KUKA KRL converter
 *
 * Architecture: PNT_LIN as DEFFCT (returns E6POS, LIN executed externally)
 *                t[10] pre-read buffer for ADVANCE=3 safe operation
 *                BLOCK 1/2/3/4 structure (see 3DP_gcode2krl_架构规范.md)
 *
 * Usage: node index.js <input.gcode> <output.src> [--name=JOBNAME] [--start-config=config.json]
 */

const fs = require('fs');
const path = require('path');

// ──────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────
function setVal(v, pre) {
  if (v === undefined || v === null) return pre !== undefined ? pre.toString() : '0';
  return v.toString();
}

/**
 * Parse a GCode line into { cmd, params: { X, Y, Z, E, F, S, ... } }
 */
function parseGCode(line) {
  line = line.split(';')[0].trim();
  if (!line) return null;

  const parts = line.split(/\s+/);
  const cmd = parts[0].toUpperCase();
  const params = {};

  for (let i = 1; i < parts.length; i++) {
    const key = parts[i][0].toUpperCase();
    const val = parseFloat(parts[i].slice(1));
    if (!isNaN(val)) params[key] = val;
  }
  return { cmd, params };
}

// ──────────────────────────────────────────
// START POSITION config
// ──────────────────────────────────────────
let startConfig = null;
const DEFAULT_START = {
  axis: { A1: 5, A2: -85.75, A3: 109.02, A4: 0.30, A5: 64.18, A6: 2.93, E1: 0, E2: 0, E3: 0, E4: 0 },
  cart: { X: 0, Y: 0, Z: 0, A: 112, B: 20.03, C: -177.85, E1: 0, E2: 0, E3: 0, E4: 0 },
  speed: 0.25,
  cdis: 100,
  advance: 3,
  heat: { T1: 200, T2: 210, T3: 220, T4: 230, T5: 240, T6: 250 },
  heatTimeout: 600
};

function loadStartConfig(cfgPath) {
  try {
    const raw = fs.readFileSync(cfgPath, 'utf-8');
    const cfg = JSON.parse(raw);
    startConfig = { ...DEFAULT_START, ...cfg };
    if (cfg.axis) startConfig.axis = { ...DEFAULT_START.axis, ...cfg.axis };
    if (cfg.cart) startConfig.cart = { ...DEFAULT_START.cart, ...cfg.cart };
    if (cfg.heat)  startConfig.heat  = { ...DEFAULT_START.heat,  ...cfg.heat };
  } catch (e) {
    console.error(`Warning: could not load start config (${cfgPath}), using defaults. ${e.message}`);
    startConfig = { ...DEFAULT_START };
  }
}
function sc() { return startConfig || DEFAULT_START; }

// ──────────────────────────────────────────
// BLOCK 1: Motion Init (fixed template)
// ──────────────────────────────────────────
function block1_MotionInit(cfg) {
  const ax = cfg.axis;
  const ct = cfg.cart;
  return [
    '  ;═══════════════════════════════════════════════════════════',
    '  ; BLOCK 1 — Motion Init (must be in main, not in subroutine)',
    '  ;═══════════════════════════════════════════════════════════',
    '  ;FOLD INI',
    '  GLOBAL INTERRUPT DECL 3 WHEN $STOPMESS==TRUE DO IR_STOPM()',
    '  INTERRUPT ON 3',
    '  BAS(#INITMOV, 0)',
    '  ;ENDFOLD',
    '',
    '  ;FOLD STARTPOSITION',
    '  $BWDSTART = FALSE',
    '  PDAT_ACT = {VEL 20, ACC 100, APO_DIST 50}',
    '  FDAT_ACT = {TOOL_NO 1, BASE_NO 1, IPO_FRAME #BASE}',
    '  BAS(#PTP_PARAMS, 20)',
    `  PTP {A1 ${setVal(ax.A1)}, A2 ${setVal(ax.A2)}, A3 ${setVal(ax.A3)}, A4 ${setVal(ax.A4)}, A5 ${setVal(ax.A5)}, A6 ${setVal(ax.A6)}, E1 ${setVal(ax.E1)}, E2 ${setVal(ax.E2)}, E3 ${setVal(ax.E3)}, E4 ${setVal(ax.E4)}}`,
    `  $VEL.CP = 0.04`,
    `  LIN {X ${setVal(ct.X)}, Y ${setVal(ct.Y)}, Z ${setVal(ct.Z)}, A ${setVal(ct.A)}, B ${setVal(ct.B)}, C ${setVal(ct.C)}, E1 ${setVal(ct.E1)}, E2 ${setVal(ct.E2)}, E3 ${setVal(ct.E3)}, E4 ${setVal(ct.E4)}}`,
    '  ;ENDFOLD',
    '',
    '  ;FOLD LIN SPEED',
    `  $VEL.CP = ${cfg.speed}`,
    `  $APO.CDIS = ${cfg.cdis}`,
    `  $ADVANCE = ${cfg.advance}`,
    '  ;ENDFOLD',
    '',
  ];
}

// ──────────────────────────────────────────
// BLOCK 2: Logic Init
// ──────────────────────────────────────────
function block2_LogicInit(cfg) {
  const h = cfg.heat;
  const lines = [
    '  ;═══════════════════════════════════════════════════════════',
    '  ; BLOCK 2 — Logic Init (subroutine calls)',
    '  ;═══════════════════════════════════════════════════════════',
    '  PNT_INIT()',
  ];

  const temps = [h.T1, h.T2, h.T3, h.T4, h.T5, h.T6]
    .map(t => t > 0 ? t : 0);
  const activeZones = temps.filter(t => t > 0);

  if (activeZones.length > 0) {
    lines.push(`  PNT_HEAT_ALL(${temps.join(', ')})`);
    lines.push(`  PNT_HEAT_WAIT_ALL(${cfg.heatTimeout})`);
  }

  lines.push('  PNT_G92_E0()');
  lines.push('  PNT_UNRETRACT()');
  lines.push('  PNT_FAN_ON()');
  lines.push('');
  return lines;
}

// ──────────────────────────────────────────
// BLOCK 4: Shutdown
// ──────────────────────────────────────────
function block4_Shutdown() {
  return [
    '  ;═══════════════════════════════════════════════════════════',
    '  ; BLOCK 4 — Shutdown',
    '  ;═══════════════════════════════════════════════════════════',
    '  PNT_RETRACT()',
    '  PNT_FAN_OFF()',
    '  PNT_HEAT_ALL(0, 0, 0, 0, 0, 0)',
    '  PNT_FINISH()',
  ];
}

// ──────────────────────────────────────────
// E-value state tracker
// ──────────────────────────────────────────
class EState {
  constructor() { this.absolute = 0; this.relative = false; this.lastCmd = 0; }
  setRelative(v) { this.relative = (v === 1); }
  reset()       { this.absolute = 0; }
  applyE(e) {
    if (e === undefined || e === null) return this.absolute;
    if (this.relative) this.absolute += e;
    else this.absolute = e;
    return this.absolute;
  }
}

// ──────────────────────────────────────────
// Main GCode→KRL converter
// ──────────────────────────────────────────
function convert(inputPath, outputPath, jobName) {
  const input  = fs.readFileSync(inputPath, 'utf-8');
  const lines  = input.split(/\r?\n/);

  const cfg = sc();
  const out = [];
  const eState = new EState();
  const fname = jobName || path.basename(outputPath, '.src');

  // ── file header ──
  out.push('&ACCESS RVP');
  out.push('&REL 3');
  out.push(`&COMMENT Generated by gcode2krl v3.0 from ${path.basename(inputPath)}`);
  out.push('&PARAM EDITMASK = *');
  out.push(`DEF ${fname}()`);
  out.push('  DECL E6POS t[10]        ; pre-read buffer (ADVANCE=3 safe)');
  out.push('');

  // ── BLOCK 1 ──
  out.push(...block1_MotionInit(cfg));

  // ── BLOCK 2 ──
  out.push(...block2_LogicInit(cfg));

  // ── Print info header ──
  out.push('  ;═══════════════════════════════════════════════════════════');
  out.push('  ; BLOCK 3 — Print Path (gcode2krl generated)');
  out.push('  ;═══════════════════════════════════════════════════════════');

  // Parse print info from GCode comments
  let printTime = '', printWeight = '', printSize = '';
  let filamentType = '', nozzleDiam = '', layerHeight = '';
  for (const raw of lines) {
    const t = raw.trim();
    if (t.startsWith('; estimated printing time')) printTime = t.split(':').slice(1).join(':').trim();
    if (t.startsWith('; total filament weight'))  printWeight = t.split(':').slice(1).join(':').trim();
    if (t.startsWith('; filament_type ='))       filamentType = t.split('=')[1]?.trim() || '';
    if (t.startsWith('; nozzle_diameter ='))     nozzleDiam = t.split('=')[1]?.trim() || '';
    if (t.startsWith('; layer_height ='))        layerHeight = t.split('=')[1]?.trim() || '';
    if (t.startsWith('; model_bounding_box'))    printSize = t.split(':').slice(1).join(':').trim();
  }
  if (printTime)   out.push(`  ;PrintTime = ${printTime}`);
  if (printWeight) out.push(`  ;PrintWeight = ${printWeight}`);
  if (printSize)   out.push(`  ;PrintSize = (X,Y,Z) = (${printSize})`);
  if (filamentType) out.push(`  ;Material = ${filamentType}`);
  if (nozzleDiam)   out.push(`  ;Nozzle = ${nozzleDiam}mm`);
  if (layerHeight)  out.push(`  ;LayerH = ${layerHeight}mm`);
  out.push('');

  // ── BLOCK 3 state machine ──
  let tIdx         = 0;        // t[n] index, 1-10 cycling
  let layerCount   = 0;
  let totalLayers  = 0;
  let firstLayer   = true;
  let fanOn        = false;
  let prevZ        = null;
  let retracting   = false;

  // count total layers first
  for (const raw of lines) {
    if (raw.match(/^;LAYER_CHANGE/i) || raw.match(/^;Z:/i)) totalLayers++;
  }

  function nextT() { tIdx = (tIdx % 10) + 1; return tIdx; }

  for (const raw of lines) {
    // forward layer-related comments, skip others
    if (raw.startsWith(';')) {
      if (raw.match(/^;(LAYER_CHANGE|Z:|HEIGHT:|TYPE:|WIPE_|PrintTime|Progress|S = )/)) {
        // pass through — handled below
      } else {
        continue;
      }
    }

    const parsed = parseGCode(raw);
    if (!parsed) continue;

    const { cmd, params } = parsed;

    // ── M82/M83: E-axis mode ──
    if (cmd === 'M82') { eState.setRelative(0); continue; }
    if (cmd === 'M83') { eState.setRelative(1); continue; }

    // ── G92 E0 ──
    if (cmd === 'G92' && params.E !== undefined && params.E === 0) {
      eState.reset();
      out.push('  PNT_G92_E0()');
      continue;
    }

    // ── Fan control ──
    if (cmd === 'M106') {
      const s = (params.S !== undefined) ? params.S : 255;
      if (s === 0) {
        if (fanOn) out.push('  PNT_FAN_OFF()');
        fanOn = false;
      } else {
        if (!fanOn) out.push(`  PNT_FAN_SET(${s})`);
        fanOn = true;
      }
      continue;
    }
    if (cmd === 'M107') {
      if (fanOn) out.push('  PNT_FAN_OFF()');
      fanOn = false;
      continue;
    }

    // ── Layer change detection ──
    const isLayerChange = raw.match(/^;(LAYER_CHANGE|Z:)/i);
    const isWipeStart   = raw.match(/^;WIPE_START/i);
    const isWipeEnd     = raw.match(/^;WIPE_END/i);
    const isTypeTag     = raw.match(/^; TYPE:/i);

    if (isLayerChange) {
      if (raw.match(/^;Z:/i)) {
        const zVal = parseFloat(raw.split(':')[1]?.trim());
        if (zVal !== null && prevZ !== null && zVal > prevZ) layerCount++;
        prevZ = zVal;
      } else {
        layerCount++;
      }

      const pct = totalLayers > 0 ? Math.round((layerCount / totalLayers) * 100) : 0;
      out.push('');
      out.push(`  ;Layer ${layerCount}`);
      out.push(`  ;Progress = ${pct}`);
      out.push('  PNT_G92_E0()');

      if (firstLayer) {
        firstLayer = false;
      } else {
        out.push('  PNT_RETRACT()');
        retracting = true;
      }
      continue;
    }

    // Retract→G0→Unretract sequence
    if (retracting && cmd === 'G0') {
      const x = params.X, y = params.Y, z = params.Z;
      const vel = params.F ? Math.round(params.F / 60) : 300;
      out.push(`  PNT_G0(${setVal(x, 0)}, ${setVal(y, 0)}, ${setVal(z, prevZ)}, ${setVal(null, 0)}, ${setVal(null, 0)}, ${setVal(null, 0)}, ${vel})`);
      out.push('  PNT_UNRETRACT()');
      retracting = false;
      continue;
    }

    if (isWipeStart) { out.push('  ; WIPE_START'); continue; }
    if (isWipeEnd)   { out.push('  ; WIPE_END');   continue; }
    if (isTypeTag)   { out.push(`  ${raw.trim()}`); continue; }

    // ── G0: Travel ──
    if (cmd === 'G0') {
      const x = params.X, y = params.Y, z = params.Z;
      const vel = params.F ? Math.round(params.F / 60) : 300;
      out.push(`  PNT_G0(${setVal(x, 0)}, ${setVal(y, 0)}, ${setVal(z, prevZ)}, ${setVal(null, 0)}, ${setVal(null, 0)}, ${setVal(null, 0)}, ${vel})`);
      continue;
    }

    // ── G1: Print move (use DEFFCT PNT_LIN + external LIN) ──
    if (cmd === 'G1') {
      const x = params.X, y = params.Y, z = params.Z;
      const e = eState.applyE(params.E);
      const vel = params.F ? Math.round(params.F / 60) : 50;
      const idx = nextT();

      out.push(`  t[${idx}] = PNT_LIN(${setVal(x, 0)}, ${setVal(y, 0)}, ${setVal(z, prevZ)}, ${setVal(null, 0)}, ${setVal(null, 0)}, ${setVal(null, 0)}, ${setVal(e)}, ${vel})`);
      out.push(`  LIN t[${idx}] C_DIS`);
      continue;
    }
  }

  // ── BLOCK 4 ──
  out.push('');
  out.push(...block4_Shutdown());

  out.push('END');
  return out;
}

// ──────────────────────────────────────────
// Write output
// ──────────────────────────────────────────
function outputSrc(filepath, lines) {
  const dir = path.dirname(filepath);
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(filepath, lines.join('\r\n'), 'utf-8');
  console.log(`Generated: ${filepath} (${lines.length} lines)`);
}

// ──────────────────────────────────────────
// CLI entry
// ──────────────────────────────────────────
function main() {
  const args = process.argv.slice(2);
  if (args.length < 2) {
    console.log('Usage: node index.js <input.gcode> <output.src> [--name=JOBNAME] [--start-config=config.json]');
    process.exit(1);
  }

  const inputPath  = args[0];
  const outputPath = args[1];
  let jobName = '';

  for (let i = 2; i < args.length; i++) {
    if (args[i].startsWith('--name=')) {
      jobName = args[i].split('=')[1];
    } else if (args[i].startsWith('--start-config=')) {
      loadStartConfig(args[i].split('=')[1]);
    }
  }

  if (!jobName) {
    jobName = path.basename(outputPath, '.src').replace(/[^a-zA-Z0-9_]/g, '_');
  }

  const linesArray = convert(inputPath, outputPath, jobName);
  outputSrc(outputPath, linesArray);
}

main();
