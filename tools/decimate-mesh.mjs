// Stage two: mesh decimation. Textures are handled separately, in their own
// process — see shrink-textures.mjs for why that is not optional.
//
// Mesh optimisation for the Meshy exports.
//
// Meshy hands back raw sculpts. The largest bed was 1.73M triangles and 138 MB
// with no textures and no UVs at all — the whole file was position and normal
// data. Others are the mirror image: 35k triangles and 31 MB, where the
// geometry is fine and every byte is a 4K texture. Both need handling, and
// which one dominates is not guessable from the file size.
//
// Four problems in total: GitHub's 100 MiB per-file hard limit, parse time at
// load, VRAM, and the fact that the player sees most of this at roughly two
// hundred pixels behind a cutaway wall.
//
// The CLI alone cannot do the geometry half. `weld` in gltf-transform v4
// merges only bitwise-identical vertices, and these meshes are faceted — every
// triangle carries its own normals, so no two vertices match and welding
// removed 32 triangles out of 1.7 million. Without welding there are no shared
// edges, and without shared edges `simplify` has nothing to collapse. So:
// strip normals, weld on position alone, decimate, regenerate normals.

import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import { weld, simplify, normals, dedup, prune } from '@gltf-transform/functions';
import { statSync } from 'node:fs';

const [, , inPath, outPath, ratioArg, errorArg] = process.argv;

if (!inPath || !outPath) {
    console.error('usage: decimate-mesh.mjs <in.glb> <out.glb> [ratio] [error]');
    process.exit(1);
}

const ratio = Number(ratioArg ?? 0.05);
const error = Number(errorArg ?? 0.01);

/// Below this, decimating costs visible quality and saves almost nothing.
/// The 46 MB merged-animation rig is 10k triangles — all of its weight is
/// texture, and a first pass that decimated it anyway made the file *larger*.
const MIN_TRIANGLES_TO_SIMPLIFY = 20_000;

// ALL_EXTENSIONS matters. Without it the reader drops anything it does not
// recognise, and a first pass silently discarded KHR_materials_specular and
// KHR_materials_ior from every character — changing how they shade, for no
// reason connected to file size.
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS);
const document = await io.read(inPath);

const countTriangles = () => document.getRoot()
    .listMeshes()
    .flatMap((mesh) => mesh.listPrimitives())
    .reduce((sum, prim) => sum +
        (prim.getIndices()?.getCount() ?? prim.getAttribute('POSITION')?.getCount() ?? 0) / 3, 0);

const before = countTriangles();

const transforms = [];

if (before >= MIN_TRIANGLES_TO_SIMPLIFY) {
    // Strip NORMAL everywhere. This is what makes welding — and therefore
    // simplification — possible at all on a faceted mesh.
    for (const mesh of document.getRoot().listMeshes()) {
        for (const primitive of mesh.listPrimitives()) {
            primitive.setAttribute('NORMAL', null);
        }
    }

    // Imported here, not at the top. The meshoptimizer WASM heap is
    // allocated on import, and with it resident every texture resize above
    // fails with a spurious "colourspace" error. Loading it only once the
    // textures are already done makes the whole pipeline reliable.
    const { MeshoptSimplifier } = await import('meshoptimizer');
    await MeshoptSimplifier.ready;

    transforms.push(
        weld(),
        simplify({ simplifier: MeshoptSimplifier, ratio, error }),
        normals({ overwrite: true }),
    );
}

transforms.push(dedup(), prune());

await document.transform(...transforms);

const after = countTriangles();
await io.write(outPath, document);

const mb = (p) => (statSync(p).size / 1048576).toFixed(1);
const n = (v) => Math.round(v).toLocaleString();

console.log(
    `${inPath.split(/[\\/]/).pop()}  ` +
    `${mb(inPath)} MB -> ${mb(outPath)} MB  ` +
    `${n(before)} -> ${n(after)} tris`
);
