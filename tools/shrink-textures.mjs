// Stage one: resize oversized textures. Runs in its own process, on purpose.
//
// Importing `@gltf-transform/functions` anywhere in the process breaks sharp:
// every resize then fails with "colourspace: parameter space not set",
// including textures that resize perfectly in a process without that import.
// The two packages bring in libvips bindings that do not co-exist. It reads
// like a colour-management problem and it is a module-loading problem, which
// cost a long time to find — hence this file existing at all.
//
// So: textures here, geometry in decimate-mesh.mjs, chained by decimate-all.

import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import sharp from 'sharp';
import { statSync } from 'node:fs';

sharp.cache(false);
sharp.concurrency(1);

const [, , inPath, outPath, maxArg] = process.argv;
const maxSize = Number(maxArg ?? 1024);

const io = new NodeIO().registerExtensions(ALL_EXTENSIONS);
const document = await io.read(inPath);

let resized = 0;
let failed = 0;

for (const texture of document.getRoot().listTextures()) {
    const image = texture.getImage();
    if (!image) continue;

    try {
        const buffer = Buffer.from(image);
        const meta = await sharp(buffer).metadata();

        if (Math.max(meta.width ?? 0, meta.height ?? 0) <= maxSize) continue;

        // A fresh instance — not the one metadata() consumed.
        const pipeline = sharp(buffer, { unlimited: true })
            .resize(maxSize, maxSize, { fit: 'inside', withoutEnlargement: true });

        // Keep alpha where it exists; JPEG would discard it silently.
        const encoded = meta.hasAlpha
            ? await pipeline.png({ compressionLevel: 9 }).toBuffer()
            : await pipeline.jpeg({ quality: 88 }).toBuffer();

        texture.setImage(new Uint8Array(encoded));
        texture.setMimeType(meta.hasAlpha ? 'image/png' : 'image/jpeg');
        resized++;
    } catch (err) {
        // One bad texture skips itself rather than losing the whole asset.
        console.error(`  texture skipped (${texture.getName() || 'unnamed'}): ` +
                      `${String(err.message).split('\n')[0]}`);
        failed++;
    }
}

await io.write(outPath, document);

const mb = (p) => (statSync(p).size / 1048576).toFixed(1);
console.log(`  textures: ${resized} resized, ${failed} skipped  ` +
            `(${mb(inPath)} MB -> ${mb(outPath)} MB)`);
