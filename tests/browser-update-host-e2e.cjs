// Real isolated Edge MV3 + actual C# Store Stage/Commit + authenticated random
// loopback port + actual library save. Never uses production profiles/port/data.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const {spawn} = require('node:child_process');
const {once} = require('node:events');
const readline = require('node:readline');
const {chromium} = require('playwright');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(read, label, timeout = 15000) {
  const end = Date.now() + timeout;let last;
  while (Date.now() < end) {
    try {last = await read();if (last) return last;} catch (error) {last = error;}
    await pause(60);
  }
  throw Error(`${label}: ${last instanceof Error ? last.message : JSON.stringify(last)}`);
}
(async () => {
  const repo = path.resolve(__dirname, '..');
  const runtime = path.resolve(repo, process.argv[2] || 'test-output/extension-host-e2e-20261013');
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'MoriDesk-update-host-'));
  const host = spawn(path.join(runtime, 'Mori.exe'), ['--browser-update-host-test', temp], {
    windowsHide: true, stdio: ['pipe', 'pipe', 'pipe']
  });
  const messages = [];let errors = '', spawnError;
  host.stderr.on('data', value => errors += value);
  host.on('error', error => {spawnError = error;});
  readline.createInterface({input: host.stdout}).on('line', line => {
    try {messages.push(JSON.parse(line));} catch {errors += line + '\n';}
  });
  const exit = once(host, 'exit');
  const receive = kind => until(() => {
    if (spawnError) throw spawnError;
    const index = messages.findIndex(message => message.kind === kind);
    if (index >= 0) return messages.splice(index, 1)[0];
    if (host.exitCode !== null) throw Error(`Host exited ${host.exitCode}: ${errors}`);
  }, 'host ' + kind);
  const command = async kind => {host.stdin.write(kind + '\n');return receive(kind === 'stage' ? 'staged' : kind);};
  let browser;
  try {
    const boot = await receive('ready');
    assert.notEqual(boot.port, 47891, 'must not use production port');
    assert(boot.path.startsWith(temp + path.sep));
    browser = await chromium.launchPersistentContext(path.join(temp, 'profile'), {
      channel: 'msedge', headless: true, viewport: {width: 1000, height: 740},
      ignoreDefaultArgs: ['--disable-extensions'],
      args: ['--disable-extensions-except=' + boot.path, '--load-extension=' + boot.path]
    });
    let worker = browser.serviceWorkers()[0] || await browser.waitForEvent('serviceworker');
    assert.equal(new URL(worker.url()).host, boot.id);
    const bridge = await worker.evaluate(() => native({action: 'bridgeStatus'}));
    assert.equal(bridge.ok, true, JSON.stringify(bridge));
    await worker.evaluate(() => updater.poll());
    const initial = await command('inspect');
    assert.equal(initial.libraryExists, false, 'control-only requests do not initialize a library');
    assert.equal(initial.settingsExists, false, 'control-only requests do not save settings');
    console.log('PASS real C# authenticated control path leaves library/settings absent');

    const preferences = {theme: 'dark', language: 'en', collector: 'popup', editBeforeSave: false,
      disabledPages: ['https://unrelated.invalid/private'], disabledHosts: ['excluded.invalid']};
    await worker.evaluate(prefs => MoriPreferences.write(prefs), preferences);
    const page = await browser.newPage();const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.message));
    let navigations = 0;page.on('framenavigated', frame => {if (frame === page.mainFrame()) navigations++;});
    const image = 'data:image/png;base64,' + fs.readFileSync(path.join(repo, 'dependencies/qingjie-icon.png')).toString('base64');
    await page.route('https://mori-update-host-fixture.invalid/**', route => route.fulfill({
      contentType: 'text/html', body: `<!doctype html><title>Host update fixture</title><img alt="Host update fixture image" style="width:120px;height:100px;margin:32px" src="${image}">`
    }));
    await page.goto('https://mori-update-host-fixture.invalid/page');
    const ready = version => until(async () => {
      const pong = await worker.evaluate(async () => {
        const tab = (await chrome.tabs.query({url: 'https://mori-update-host-fixture.invalid/*'}))[0];
        return chrome.tabs.sendMessage(tab.id, {action: 'mori-collector-ping'});
      });
      return pong.ready && pong.version === version;
    }, 'collector ' + version);
    const startDrag = async () => {
      await page.mouse.move(70, 70);await page.mouse.down();await page.mouse.move(100, 105, {steps: 7});
      await page.waitForSelector('[data-mori-collector]');
    };
    await ready(boot.version);
    await page.evaluate(() => {window.hostFixtureMarker = 'unchanged';});
    await startDrag();await command('stage');
    assert.equal((await worker.evaluate(() => updater.poll())).reason, 'deferred');
    const staged = await command('inspect');
    assert.equal(staged.version, boot.version);assert.equal(staged.pending, true);assert.equal(staged.applies, 0);
    assert.equal(await page.locator('[data-mori-collector]').count(), 1);
    console.log('PASS actual C# Stage waits while a real drag is active');

    const beforeNavigations = navigations;
    const newWorker = browser.waitForEvent('serviceworker', {timeout: 15000});
    await page.keyboard.press('Escape');await page.mouse.up();
    await page.waitForSelector('[data-mori-collector]', {state: 'detached'});
    await worker.evaluate(() => updater.poll()).catch(error => {
      if (!/closed|destroyed|Target|Execution context/i.test(error.message)) throw error;
    });
    worker = await newWorker;
    assert.equal(new URL(worker.url()).host, boot.id);
    await ready(boot.nextVersion);
    await worker.evaluate(() => updater.poll());
    const applied = await command('inspect');
    assert.equal(applied.version, boot.nextVersion);assert.equal(applied.activeVersion, boot.nextVersion);
    assert.equal(applied.pending, false);assert.equal(applied.awaitingReload, false);
    assert.equal(applied.backup, true);assert.equal(applied.applies, 1);
    assert.equal(await page.evaluate(() => window.hostFixtureMarker), 'unchanged');
    assert.equal(navigations, beforeNavigations);
    assert.deepEqual(await worker.evaluate(() => MoriPreferences.read()), preferences);
    console.log('PASS actual C# Commit, runtime reload, new-version ACK, backup, stable ID/preferences and unchanged open page');

    await startDrag();await page.mouse.move(260, 220, {steps: 8});await page.mouse.up();
    await page.waitForSelector('[data-mori-collector]', {state: 'detached'});
    await until(async () => (await command('inspect')).count === 1, 'real native save');
    await until(async () => !(await worker.evaluate(() => updater.status())).busy, 'save journal settled');
    await pause(300);
    const final = await command('finish');
    assert.equal(final.count, 1);assert.equal(final.source, true);assert.equal(final.saves, 1);
    assert.equal(final.applies, 1);assert.equal(final.settingsExists, false);
    const [code] = await exit;assert.equal(code, 0, errors);
    assert.deepEqual(pageErrors, []);
    console.log('PASS post-update drag saves exactly one actual asset with source; no duplicate handler/reload loop');
    console.log('PASS isolated real host + Edge update e2e; fixture: ' + temp);
  } finally {
    await browser?.close();
    if (host.exitCode === null) host.kill();
  }
})().catch(error => {console.error(error);process.exitCode = 1;});
