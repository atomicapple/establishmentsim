// Batch-decimate every .glb under Assets/, writing to a staging mirror.
//
// Never writes over an input. The four ornate beds are excluded from git, so
// for those the working copy is the only copy and an in-place pass would
// destroy an irreplaceable file on any bug. Staging first also means the whole
// set can be inspected before a single asset in the game changes.
//
// Ratios differ by what the thing is and how close the camera ever gets:
// characters carry the scene and are seen at full height, furniture is dressing
// behind a cutaway wall.

import { execFileSync } from 'node:child_process';
import { readdirSync, statSync, mkdirSync, copyFileSync, rmSync } from 'node:fs';
import { join, relative, dirname, sep } from 'node:path';

const SOURCE = 'Assets';
const STAGE = 'Assets_optimised';

/// Anything below this is already a sensible size and is copied untouched.
/// Re-decimating a 300 KB model costs quality and saves nothing.
const SKIP_BELOW_MB = 8;

function textureCapFor(path) {
    const p = path.toLowerCase();
    // Faces are looked at; a rug is walked on and seen at a slant.
    if (p.includes('characters')) return 2048;
    return 1024;
}

function ratioFor(path) {
    const p = path.toLowerCase();

    // Rigged and animated, and the player looks straight at them.
    if (p.includes(`characters${sep}`) || p.includes('/characters/')) {
        return { ratio: 0.25, error: 0.004 };
    }

    // Wall art is flat and read at a distance; geometry is nearly all waste.
    if (p.includes('walldecoration')) return { ratio: 0.05, error: 0.02 };

    // Furniture: seen small, behind a cutaway, often partly occluded.
    return { ratio: 0.05, error: 0.01 };
}

function walk(dir) {
    const out = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) out.push(...walk(full));
        else if (entry.name.toLowerCase().endsWith('.glb')) out.push(full);
    }
    return out;
}

const files = walk(SOURCE).sort((a, b) => statSync(b).size - statSync(a).size);

let totalBefore = 0;
let totalAfter = 0;
let processed = 0;
let copied = 0;
let failed = 0;

for (const file of files) {
    const sizeMb = statSync(file).size / 1048576;
    const target = join(STAGE, relative(SOURCE, file));

    mkdirSync(dirname(target), { recursive: true });
    totalBefore += sizeMb;

    if (sizeMb < SKIP_BELOW_MB) {
        copyFileSync(file, target);
        totalAfter += sizeMb;
        copied++;
        continue;
    }

    const { ratio, error } = ratioFor(file);
    const cap = textureCapFor(file);
    const scratch = `${target}.textures.glb`;

    const run = (script, args) => execFileSync(
        'node', [script, ...args],
        { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }
    );

    try {
        // Two processes, not two function calls. Importing
        // @gltf-transform/functions breaks sharp in the same process — every
        // texture resize then fails with a spurious "colourspace: parameter
        // space not set" — so the texture stage has to run somewhere that
        // never loads it.
        const textureLog = run('tools/shrink-textures.mjs', [file, scratch, String(cap)]);
        const meshLog = run('tools/decimate-mesh.mjs', [scratch, target, String(ratio), String(error)]);

        rmSync(scratch, { force: true });

        for (const line of textureLog.split('\n')) {
            if (line.includes('textures:')) console.log(line.trimEnd());
        }

        console.log(meshLog.trim().split('\n').pop());
        totalAfter += statSync(target).size / 1048576;
        processed++;
    } catch (err) {
        // A model that will not optimise is copied through unchanged rather
        // than dropped. A missing asset is a worse outcome than a large one.
        console.error(`FAILED ${file}: ${String(err.message).split('\n')[0]}`);
        rmSync(scratch, { force: true });
        copyFileSync(file, target);
        totalAfter += sizeMb;
        failed++;
    }
}

console.log(
    `\n${processed} decimated, ${copied} copied as-is, ${failed} failed\n` +
    `${totalBefore.toFixed(0)} MB -> ${totalAfter.toFixed(0)} MB`
);
