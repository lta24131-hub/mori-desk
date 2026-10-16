import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join, relative } from 'node:path';
import { readFile, readdir, stat, mkdir, cp, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { build } from 'vite';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(root, '../canvas');
const pinned = '2.4.6';
const pinnedLicenseHash = 'd113f392e813269f31c5af93d5e61ba4dccccdceecc014c15cb0c64010cc9533';
const requireFromRoot = createRequire(join(root, 'package.json'));
const packages = new Map();

async function packageAt(name, resolver) {
  let file;
  try { file = resolver.resolve(`${name}/package.json`); }
  catch { file = resolver.resolve(name); }
  let folder = dirname(file);
  while (dirname(folder) !== folder) {
    try {
      const metadata = JSON.parse(await readFile(join(folder, 'package.json'), 'utf8'));
      if (metadata.name === name) return { folder, metadata };
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    folder = dirname(folder);
  }
  throw new Error(`Cannot resolve package metadata: ${name}`);
}

async function visit(name, resolver) {
  const item = await packageAt(name, resolver);
  const { metadata, folder } = item;
  const key = `${metadata.name}@${metadata.version}`;
  if (packages.has(key)) return item;
  if ((name === 'tldraw' || name.startsWith('@tldraw/')) && metadata.version !== pinned) throw new Error(`Pinned canvas version mismatch: ${key}`);
  packages.set(key, item);
  const childRequire = createRequire(join(folder, 'package.json'));
  for (const dependency of Object.keys(metadata.dependencies || {})) await visit(dependency, childRequire);
  for (const dependency of Object.keys(metadata.optionalDependencies || {})) {
    try { await visit(dependency, childRequire); }
    catch (error) { if (error.code !== 'MODULE_NOT_FOUND') throw error; }
  }
  return item;
}

const sourceMetadata = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'));
for (const name of Object.keys(sourceMetadata.dependencies)) await visit(name, requireFromRoot);
const license = await readFile(join(root, 'licenses/tldraw-2.4.6-LICENSE.md'));
if (createHash('sha256').update(license).digest('hex') !== pinnedLicenseHash) throw new Error('Pinned full license must remain byte-for-byte identical to immutable upstream source.');

// Vite may clear only our own generated web/canvas folder, never source or data.
if (output !== resolve(root, '../canvas') || !relative(dirname(root), output).startsWith('canvas')) throw new Error('Unexpected canvas output path.');
await build({ root, configFile: join(root, 'vite.config.ts') });
const { folder: assetFolder } = await packageAt('@tldraw/assets', requireFromRoot);
for (const directory of ['fonts', 'translations', 'embed-icons']) await cp(join(assetFolder, directory), join(output, 'tl-assets', directory), { recursive: true });
await mkdir(join(output, 'tl-assets/icons/icon'), { recursive: true });
await cp(join(assetFolder, 'icons/icon/0_merged.svg'), join(output, 'tl-assets/icons/icon/0_merged.svg'));
await cp(join(root, 'licenses'), join(output, 'licenses'), { recursive: true });
await cp(join(root, 'THIRD-PARTY-NOTICES.txt'), join(output, 'THIRD-PARTY-NOTICES.txt'));
await cp(join(root, 'licenses/tldraw-2.4.6-LICENSE.md'), join(output, 'LICENSE.md'));

const inventory = [];
for (const [key, { metadata, folder }] of [...packages].sort(([a], [b]) => a.localeCompare(b))) {
  const directory = key.replace(/[^a-zA-Z0-9._-]/g, '_');
  const files = (await readdir(folder)).filter(name => /^(?:licen[cs]e|copying|notice|copyright)(?:[._-].*)?$/i.test(name));
  const copied = [];
  for (const file of files) {
    if (!(await stat(join(folder, file))).isFile()) continue;
    await mkdir(join(output, 'licenses/npm', directory), { recursive: true });
    await cp(join(folder, file), join(output, 'licenses/npm', directory, file));
    copied.push(`npm/${directory}/${file}`);
  }
  if (!copied.length && metadata.name.startsWith('@radix-ui/') && metadata.license === 'MIT') copied.push('radix-ui-primitives-MIT.txt');
  // 2.3.8's npm tarball declares MIT but omits the license file. Preserve the
  // upstream repository's later-added full MIT file, and disclose its source.
  const repositoryLicenseSource = metadata.name === 'react-remove-scroll-bar' && metadata.version === '2.3.8' && metadata.license === 'MIT'
    ? 'https://raw.githubusercontent.com/theKashey/react-remove-scroll-bar/8ca9ba5ea52de03308fe8ced94f7b159a44d28ff/LICENSE' : undefined;
  if (!copied.length && repositoryLicenseSource) copied.push('react-remove-scroll-bar-MIT.txt');
  if (!copied.length) throw new Error(`No original license found: ${key}`);
  inventory.push({ name: metadata.name, version: metadata.version, license: metadata.license, author: metadata.author, files: copied, ...(repositoryLicenseSource ? { repositoryLicenseSource, note: 'npm tarball omits LICENSE; full MIT file retained from official upstream repository' } : {}), ...(metadata.name === 'tldraw' || metadata.name.startsWith('@tldraw/') ? { governingLicense: 'tldraw-2.4.6-LICENSE.md', immutableSource: '16ba10a88e7145a5bec772a985df5a8c5b943c74' } : {}) });
}
await writeFile(join(output, 'licenses/dependencies.json'), JSON.stringify(inventory, null, 2) + '\n');
await writeFile(join(output, 'BUILD-INFO.json'), JSON.stringify({ upstreamVersion: pinned, sourceCommit: '16ba10a88e7145a5bec772a985df5a8c5b943c74', licenseSha256: pinnedLicenseHash, protocol: 'nook-canvas-v1', selfHosted: true, sourceModule: 'web/canvas-src' }, null, 2) + '\n');
let totalBytes = 0, totalFiles = 0;
async function count(folder) { for (const item of await readdir(folder, { withFileTypes: true })) { const path = join(folder, item.name); if (item.isDirectory()) await count(path); else { totalFiles++; totalBytes += (await stat(path)).size; } } }
await count(output);
console.log(JSON.stringify({ output, totalBytes, totalFiles, runtimePackages: inventory.length, pinnedVersion: pinned }));
