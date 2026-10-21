#!/usr/bin/env node
'use strict';

// The SVGs in Ansible/Assets/Brand preserve geometry read through Pencil MCP and are the editable
// source of truth. This script only rasterizes them for package assets. It
// never rewrites a Brand SVG or a native PNG export.

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const repositoryRoot = path.resolve(__dirname, '..');
const brandDirectory = path.join(repositoryRoot, 'Ansible', 'Assets', 'Brand');
const assetsDirectory = path.join(repositoryRoot, 'Ansible', 'Assets');

function loadRenderer() {
    try {
        return require('@resvg/resvg-js');
    } catch (error) {
        const bundled = path.join(repositoryRoot, '.runtime', 'design-renderer', 'node_modules', '@resvg', 'resvg-js');
        try {
            return require(bundled);
        } catch {
            throw new Error(`@resvg/resvg-js is required. Install it in .runtime/design-renderer or the workspace. ${error.message}`);
        }
    }
}

const { Resvg } = loadRenderer();

function ensureDirectory(directory) {
    fs.mkdirSync(directory, { recursive: true });
}

function readText(filePath) {
    return fs.readFileSync(filePath, 'utf8');
}

function writeFile(filePath, contents) {
    ensureDirectory(path.dirname(filePath));
    fs.writeFileSync(filePath, contents);
}

function readSvg(name) {
    const filePath = path.join(brandDirectory, `${name}.svg`);
    if (!fs.existsSync(filePath)) throw new Error(`Missing Pencil SVG source ${path.relative(repositoryRoot, filePath)}.`);
    const svg = readText(filePath);
    const root = svg.match(/<svg\b([^>]*)>/i);
    const viewBox = root?.[1].match(/\bviewBox="([^"]+)"/i)?.[1].trim().split(/\s+/).map(Number);
    const width = Number(root?.[1].match(/\bwidth="([^"]+)"/i)?.[1]);
    const height = Number(root?.[1].match(/\bheight="([^"]+)"/i)?.[1]);
    if (!root || !viewBox || viewBox.length !== 4 || !Number.isFinite(width) || !Number.isFinite(height)) {
        throw new Error(`Invalid SVG source ${name}.svg.`);
    }
    return { name, path: filePath, svg, width, height, viewBox };
}

function renderSvg(source, width = source.width, height = source.height) {
    const rendered = new Resvg(source.svg, {
        fitTo: { mode: 'original' },
        shapeRendering: 2,
        textRendering: 2,
        imageRendering: 0,
    }).render();
    if (rendered.width !== width || rendered.height !== height) {
        throw new Error(`${source.name}.svg rendered at ${rendered.width}x${rendered.height}; expected ${width}x${height}.`);
    }
    return { png: rendered.asPng(), pixels: rendered.pixels, width: rendered.width, height: rendered.height };
}

function renderWrappedSvg(source, name, width, height, iconSize) {
    const body = source.svg.replace(/^\s*<\?xml[^>]*>\s*/i, '').replace(/^\s*<svg\b[^>]*>/i, '').replace(/<\/svg>\s*$/i, '');
    const x = (width - iconSize) / 2;
    const y = (height - iconSize) / 2;
    const wrapped = {
        name,
        width,
        height,
        svg: `<?xml version="1.0" encoding="UTF-8"?>\n<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><svg x="${x}" y="${y}" width="${iconSize}" height="${iconSize}" viewBox="${source.viewBox.join(' ')}" preserveAspectRatio="none">${body}</svg></svg>\n`,
    };
    return renderSvg(wrapped, width, height);
}

function pngInfo(buffer) {
    if (buffer.length < 24 || !buffer.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) {
        throw new Error('Invalid PNG signature.');
    }
    return { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) };
}

function validateRendered(name, rendered, expectedWidth, expectedHeight) {
    if (rendered.width !== expectedWidth || rendered.height !== expectedHeight) {
        throw new Error(`${name} rendered at ${rendered.width}x${rendered.height}, expected ${expectedWidth}x${expectedHeight}.`);
    }
    const alpha = rendered.pixels.filter((_, index) => index % 4 === 3);
    if (!alpha.some(value => value !== 0) || !alpha.some(value => value !== 255)) {
        throw new Error(`${name} does not contain the expected transparent artwork surface.`);
    }
}

function sourcePng(name, fallbackSvg, width, height) {
    const filePath = path.join(brandDirectory, `${name}.png`);
    if (fs.existsSync(filePath)) {
        const png = fs.readFileSync(filePath);
        const info = pngInfo(png);
        if (info.width !== width || info.height !== height) {
            throw new Error(`${name}.png is ${info.width}x${info.height}; expected ${width}x${height}.`);
        }
        return png;
    }
    const source = readSvg(fallbackSvg);
    const rendered = source.width === width && source.height === height
        ? renderSvg(source, width, height)
        : renderWrappedSvg(source, `${fallbackSvg}-${width}`, width, height, width);
    validateRendered(fallbackSvg, rendered, width, height);
    return rendered.png;
}

function hash(buffer) {
    return crypto.createHash('sha256').update(buffer).digest('hex');
}

function buildIco(entries) {
    const header = Buffer.alloc(6);
    header.writeUInt16LE(0, 0);
    header.writeUInt16LE(1, 2);
    header.writeUInt16LE(entries.length, 4);
    const directory = Buffer.alloc(entries.length * 16);
    let imageOffset = header.length + directory.length;
    const images = [];
    entries.forEach((entry, index) => {
        const position = index * 16;
        directory[position] = entry.width >= 256 ? 0 : entry.width;
        directory[position + 1] = entry.height >= 256 ? 0 : entry.height;
        directory.writeUInt16LE(1, position + 4);
        directory.writeUInt16LE(32, position + 6);
        directory.writeUInt32LE(entry.png.length, position + 8);
        directory.writeUInt32LE(imageOffset, position + 12);
        imageOffset += entry.png.length;
        images.push(entry.png);
    });
    return Buffer.concat([header, directory, ...images]);
}

function main() {
    ensureDirectory(brandDirectory);
    const master = readSvg('app-icon-master');
    const masterRendered = renderSvg(master, 220, 220);
    validateRendered('app-icon-master', masterRendered, 220, 220);
    const generated = [];

    const packageTargets = [
        ['Square44x44Logo.scale-200.png', 88, 88, 88],
        ['Square150x150Logo.scale-200.png', 300, 300, 300],
        ['StoreLogo.png', 50, 50, 50],
        ['Wide310x150Logo.scale-200.png', 620, 300, 300],
        ['SplashScreen.scale-200.png', 620, 300, 300],
    ];
    for (const [name, width, height, iconSize] of packageTargets) {
        const rendered = renderWrappedSvg(master, name, width, height, iconSize);
        validateRendered(name, rendered, width, height);
        writeFile(path.join(assetsDirectory, name), rendered.png);
        generated.push(`${name} ${hash(rendered.png)}`);
    }

    const lockScreen = sourcePng('app-light-48', 'app-light-48', 48, 48);
    writeFile(path.join(assetsDirectory, 'LockScreenLogo.scale-200.png'), lockScreen);
    generated.push(`LockScreenLogo.scale-200.png ${hash(lockScreen)}`);

    const nativeSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
    for (const size of nativeSizes) {
        for (const [mode, suffix] of [['dark', 'unplated'], ['light', 'lightunplated']]) {
            const name = `Square44x44Logo.targetsize-${size}_altform-${suffix}.png`;
            const png = sourcePng(`app-${mode}-${size}`, `app-${mode}-${size}`, size, size);
            writeFile(path.join(assetsDirectory, name), png);
            generated.push(`${name} ${hash(png)}`);
        }
    }

    const icoSizes = nativeSizes;
    const icoEntries = icoSizes.map(size => ({
        width: size,
        height: size,
        png: sourcePng(`app-light-${size}`,
            fs.existsSync(path.join(brandDirectory, `app-light-${size}.svg`)) ? `app-light-${size}` : 'app-icon-master',
            size, size),
    }));
    const ico = buildIco(icoEntries);
    writeFile(path.join(assetsDirectory, 'AppIcon.ico'), ico);
    generated.push(`AppIcon.ico ${hash(ico)}`);

    console.log(`Generated ${generated.length} package assets from Pencil SVG sources.`);
    console.log(`Master SVG ${path.relative(repositoryRoot, master.path)} SHA-256 ${hash(fs.readFileSync(master.path))}.`);
    console.log(`ICO entries ${icoSizes.join(', ')}.`);
}

main();
