// Nook capture-only packaging. Never invokes upstream electron-builder hooks.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const moduleRoot = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, item, i, all) => {
  if (i % 2 === 0) pairs.push([item.replace(/^--/, ''), all[i + 1]]);
  return pairs;
}, []));
for (const key of ['upstream', 'stage', 'output']) {
  if (!args[key] || !path.isAbsolute(args[key])) throw new Error(`--${key} must be absolute`);
}
const upstream = path.resolve(args.upstream), stage = path.resolve(args.stage), output = path.resolve(args.output);
for (const source of [upstream, stage, moduleRoot]) {
  const relative = path.relative(source, output);
  if (!relative || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative)))
    throw new Error('Output must not be inside a source directory');
}
if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('This package targets Windows x64 only');
if (fs.existsSync(output)) throw new Error('Output must not exist');
if (!fs.existsSync(path.join(stage, 'out/main/main.js'))) throw new Error('Build the staged helper first');
const sourceRequire = createRequire(path.join(upstream, 'package.json'));
const electronDir = path.dirname(sourceRequire.resolve('electron/package.json'));
const electronVersion = JSON.parse(fs.readFileSync(path.join(electronDir, 'package.json'), 'utf8')).version;
if (electronVersion !== '44.3.0') throw new Error('Unexpected Electron runtime version');
const dist = path.join(electronDir, 'dist');
if (!fs.existsSync(path.join(dist, 'electron.exe'))) throw new Error('Electron runtime is missing');

fs.cpSync(dist, output, { recursive: true, filter: (p) => path.basename(p) !== 'default_app.asar' });
fs.renameSync(path.join(output, 'electron.exe'), path.join(output, 'Nook.ScrollCapture.exe'));
const app = path.join(output, 'resources/app');
fs.mkdirSync(app, { recursive: true });
for (const name of ['out', 'lib/translate']) fs.cpSync(path.join(stage, name), path.join(app, name), { recursive: true });
fs.writeFileSync(path.join(app, 'package.json'), JSON.stringify({
  name: 'nook-scroll-capture', productName: 'Nook Scroll Capture', version: '15.6.0-nook.1',
  private: true, license: 'GPL-3.0-only', main: './out/main/main.js',
}, null, 2));

// All renderer JavaScript dependencies are bundled by Vite. Only the two
// native modules stay external. Follow their exact installed runtime closure,
// not eSearch's full dependencies (OCR, segmentation, recording, etc.).
const installed = new Map();
function copyRuntime(name, from = sourceRequire, optional = false) {
  let manifest;
  try { manifest = from.resolve(`${name}/package.json`); }
  catch (error) { if (optional) return; throw error; }
  const packageRoot = fs.realpathSync(path.dirname(manifest));
  const pkg = JSON.parse(fs.readFileSync(manifest, 'utf8'));
  if ((pkg.os && !pkg.os.includes('win32')) || (pkg.cpu && !pkg.cpu.includes('x64'))) {
    if (optional) return;
    throw new Error(`Unsupported required runtime: ${name}`);
  }
  if (installed.has(name)) {
    if (installed.get(name).version !== pkg.version) throw new Error(`Runtime version collision: ${name}`);
    return;
  }
  const target = path.join(app, 'node_modules', name);
  fs.cpSync(packageRoot, target, { recursive: true, dereference: true, filter: (p) => {
    const rel = path.relative(packageRoot, p).replaceAll('\\', '/');
    return !rel.startsWith('node_modules') && !/^prebuilds\/(?!win32-x64(?:\/|$))[^/]+/.test(rel);
  }});
  installed.set(name, { name, version: pkg.version, license: pkg.license || 'See LICENSE' });
  const localRequire = createRequire(manifest);
  for (const dependency of Object.keys(pkg.dependencies || {})) copyRuntime(dependency, localRequire);
  for (const dependency of Object.keys(pkg.optionalDependencies || {})) copyRuntime(dependency, localRequire, true);
}
copyRuntime('node-screenshots');
copyRuntime('uiohook-napi');
if (!installed.has('node-screenshots-win32-x64-msvc')) throw new Error('Windows screen-capture binding is missing');

// Ship complete adapted source and reproducible build instructions alongside
// the binary. No source download offer or private-repository access is needed.
const corresponding = path.join(output, 'corresponding-source');
fs.mkdirSync(corresponding, { recursive: true });
for (const entry of fs.readdirSync(stage, { withFileTypes: true })) {
  if (['node_modules', 'out', '.git'].includes(entry.name)) continue;
  fs.cpSync(path.join(stage, entry.name), path.join(corresponding, entry.name), { recursive: true });
}
const adaptation = path.join(corresponding, 'nook-adaptation');
fs.cpSync(moduleRoot, adaptation, { recursive: true, filter: (p) => !p.includes(`${path.sep}node_modules${path.sep}`) });
fs.copyFileSync(path.join(upstream, 'package.json'), path.join(corresponding, 'upstream-package.json'));
fs.copyFileSync(path.join(upstream, 'pnpm-lock.yaml'), path.join(corresponding, 'pnpm-lock.yaml'));
fs.copyFileSync(path.join(upstream, 'LICENSE'), path.join(output, 'LICENSE-eSearch.txt'));
const notices = path.join(moduleRoot, 'NOTICES.md');
if (!fs.existsSync(notices)) throw new Error('Capture component notices must be present');
fs.copyFileSync(notices, path.join(output, 'NOTICES.md'));

// Also retain the licenses of bundled JS/WASM code, including Fabric and
// picture_match; native package license files remain in resources/app.
const licenses = path.join(output, 'licenses');
fs.mkdirSync(licenses, { recursive: true });
fs.cpSync(path.join(moduleRoot, 'licenses'), licenses, { recursive: true });
// The native toolbar embeds the official Fluent checkmark during preparation.
// Keep its MIT notice beside other runtime third-party licenses, not only in
// the full corresponding-source copy.
const fluentLicense = path.join(licenses, 'MicrosoftFluentSystemIcons');
fs.mkdirSync(fluentLicense, { recursive: true });
for (const name of ['LICENSE.txt', 'SOURCE.txt'])
  fs.copyFileSync(path.join(moduleRoot, 'host/assets/fluent', name), path.join(fluentLicense, name));
const upstreamPkg = JSON.parse(fs.readFileSync(path.join(upstream, 'package.json'), 'utf8'));
for (const name of Object.keys(upstreamPkg.dependencies)) {
  const root = path.join(upstream, 'node_modules', name);
  if (!fs.existsSync(root)) continue;
  const destination = path.join(licenses, name.replaceAll('/', '__'));
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    if (entry.isFile() && /^(licen[cs]e|copying|notice)([.-]|$)/i.test(entry.name)) {
      fs.mkdirSync(destination, { recursive: true });
      fs.copyFileSync(path.join(root, entry.name), path.join(destination, entry.name));
    }
  }
}
const digest = (p) => createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const manifest = {
  schemaVersion: 1, deferredStart: true, captureFeaturePolicy: true, capturePrewarm: true, upstream: 'https://github.com/xushengfeng/eSearch', upstreamVersion: '15.6.0',
  upstreamCommit: '6f3f56241cccc8ada2c503df642566322dc67a3e', electronVersion,
  entryPoint: 'Nook.ScrollCapture.exe', application: 'resources/app/out/main/main.js',
  applicationSha256: digest(path.join(app, 'out/main/main.js')),
  runtimePackages: [...installed.values()], sourceDirectory: 'corresponding-source',
};
fs.writeFileSync(path.join(output, 'component.json'), JSON.stringify(manifest, null, 2));
console.log(JSON.stringify({ output, runtimePackages: manifest.runtimePackages, sourceIncluded: true }));
