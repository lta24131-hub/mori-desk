// Use the system HTTPS/proxy configuration while retaining Electron's pinned
// archive checksum. This restores files only; it never starts Electron.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
const upstream = path.resolve(process.argv[2] || '');
const require = createRequire(path.join(upstream, 'package.json'));
const electronPackage = require.resolve('electron/package.json');
const root = path.dirname(electronPackage);
const { version } = JSON.parse(fs.readFileSync(electronPackage, 'utf8'));
if (version !== '44.3.0' || process.platform !== 'win32' || process.arch !== 'x64')
  throw new Error('Expected the locked Electron 44.3.0 Windows x64 dependency');
const dist = path.join(root, 'dist');
if (fs.existsSync(path.join(dist, 'electron.exe')) &&
    fs.readFileSync(path.join(dist, 'version'), 'utf8').trim().replace(/^v/, '') === version) {
  console.log('Electron runtime already restored');
} else {
  const archiveName = `electron-v${version}-win32-x64.zip`;
  const archive = path.join(upstream, archiveName);
  const expected = JSON.parse(fs.readFileSync(path.join(root, 'checksums.json'), 'utf8'))[archiveName];
  if (!expected) throw new Error('Pinned archive checksum missing');
  const digest = () => createHash('sha256').update(fs.readFileSync(archive)).digest('hex');
  if (!fs.existsSync(archive) || digest() !== expected) {
    execFileSync('curl.exe', ['--fail', '--location', '--retry', '2', '--connect-timeout', '20',
      '--max-time', '600', '--output', archive,
      `https://github.com/electron/electron/releases/download/v${version}/${archiveName}`],
    { stdio: 'inherit', windowsHide: true, timeout: 650_000 });
  }
  if (digest() !== expected) throw new Error('Electron archive checksum mismatch');
  const electronRequire = createRequire(electronPackage);
  await electronRequire('@electron-internal/extract-zip').extract(archive, { dir: dist });
  fs.writeFileSync(path.join(root, 'path.txt'), 'electron.exe');
  console.log(`Verified and restored Electron ${version}`);
}
