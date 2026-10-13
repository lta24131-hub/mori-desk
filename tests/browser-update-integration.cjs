// Real Edge MV3 update/reload checks in a fresh profile. Only the temporary
// extension copy and a synthetic loopback endpoint are used; no native host,
// user profile, pairing key, settings file or media library is opened.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const crypto = require('node:crypto');
const {chromium} = require('playwright');

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(read, label, timeout = 12000) {
  const deadline = Date.now() + timeout;
  let last;
  while (Date.now() < deadline) {
    try {last = await read();if (last) return last;} catch (error) {last = error;}
    await pause(60);
  }
  throw Error(`${label}: ${last instanceof Error ? last.message : JSON.stringify(last)}`);
}

(async () => {
  const root = path.resolve(__dirname, '..');
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'Mori-update-integration-'));
  const extension = path.join(temp, 'extension');
  fs.cpSync(path.join(root, 'browser-extension'), extension, {
    recursive: true, filter: source => path.basename(source) !== 'local-connection.js'
  });
  assert(fs.existsSync(path.join(extension, 'updater.js')), 'update implementation is required');
  const manifestPath = path.join(extension, 'manifest.json');
  const originalManifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const versionParts = originalManifest.version.split('.').map(Number);
  versionParts[versionParts.length - 1]++;
  const nextVersion = versionParts.join('.');
  const state = {offer: false, applies: [], calls: [], saves: [], held: null, holdSave: false};
  const nonce = crypto.randomBytes(16).toString('hex');
  const token = crypto.randomUUID();
  const answer = (response, value) => {
    response.writeHead(200, {'Content-Type': 'application/json', 'Cache-Control': 'no-store'});
    response.end(JSON.stringify(value));
  };
  const server = http.createServer(async (request, response) => {
    try {
      if (request.method !== 'POST' || !request.url.startsWith(`/${nonce}/`)) {
        response.writeHead(404);response.end();return;
      }
      let body = '';
      for await (const chunk of request) {body += chunk;if (body.length > 1024 * 1024) throw Error('Fixture request too large');}
      const message = JSON.parse(body);
      state.calls.push({channel: request.url.split('/').pop(), action: message.action, op: message.op});
      if (request.url === `/${nonce}/control`) {
        assert.equal(message.action, 'extensionControl');
        assert.equal(message.installationId, '00000000000040008000000000000001');
        assert.match(message.extensionId, /^[a-p]{32}$/);
        assert.match(message.clientId, /^[a-f0-9]{32}$/, 'updater includes its stable client identity');
        if (message.op === 'poll') {
          answer(response, state.offer ? {ok: true, update: {token, version: nextVersion}} : {ok: true});return;
        }
        assert.equal(message.op, 'apply');assert.equal(message.token, token);assert.equal(state.offer, true);
        // Simulate only the trusted host's final apply step, then let the real
        // worker perform runtime.reload and repair the already-open page.
        const updated = {...originalManifest, version: nextVersion};
        fs.writeFileSync(manifestPath, JSON.stringify(updated, null, 2));
        state.applies.push({...message});state.offer = false;
        answer(response, {ok: true, reload: true});return;
      }
      assert.equal(request.url, `/${nonce}/native`);
      if (message.action === 'folders') {answer(response, {ok: true, folders: []});return;}
      if (message.action === 'bridgeStatus') {answer(response, {ok: true});return;}
      assert.equal(message.action, 'save', 'only synthetic saves may reach the fixture');
      state.saves.push({name: message.name, source: message.source});
      if (state.holdSave) {assert.equal(state.held, null);state.held = () => answer(response, {ok: true});return;}
      answer(response, {ok: true});
    } catch (error) {
      state.error = error;
      if (!response.headersSent) answer(response, {ok: false, error: error.message});
      else response.end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const endpoint = `http://127.0.0.1:${server.address().port}/${nonce}`;
  fs.appendFileSync(path.join(extension, 'background.js'), `
// Synthetic update-integration endpoint; this file exists only in a temp copy.
self.MoriLocalConnection = {installationId:'00000000000040008000000000000001'};
MoriLocalBridge.configured = () => true;
const fixtureRequest = async (channel, message) => {
  const response = await fetch(${JSON.stringify(endpoint)} + '/' + channel, {
    method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(message)
  });
  return response.json();
};
MoriLocalBridge.request = message => fixtureRequest('control', message);
native = async message => {
  const reply = await fixtureRequest('native', message);
  if (['save','bookmark','importBookmarks'].includes(message.action)) updater.observeWrite(reply);
  return reply;
};
`);

  let browser;
  try {
    browser = await chromium.launchPersistentContext(path.join(temp, 'profile'), {
      channel: 'msedge', headless: true, viewport: {width: 1000, height: 740},
      ignoreDefaultArgs: ['--disable-extensions'],
      args: ['--disable-extensions-except=' + extension, '--load-extension=' + extension]
    });
    let worker = browser.serviceWorkers()[0] || await browser.waitForEvent('serviceworker');
    const id = new URL(worker.url()).host;
    const preferences = {theme: 'dark', language: 'en', collector: 'popup', editBeforeSave: false,
      disabledPages: ['https://unrelated.invalid/private'], disabledHosts: ['excluded.invalid']};
    await worker.evaluate(prefs => MoriPreferences.write(prefs), preferences);
    const page = await browser.newPage();
    const errors = [];page.on('pageerror', error => errors.push(error.message));
    let navigations = 0;page.on('framenavigated', frame => {if (frame === page.mainFrame()) navigations++;});
    const image = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9YL7ZioAAAAASUVORK5CYII=';
    await page.route('https://mori-update-fixture.invalid/**', route => route.fulfill({
      contentType: 'text/html', body: `<!doctype html><title>Update fixture</title><img id="one" alt="Synthetic update image" style="width:120px;height:100px;margin:32px" src="${image}">`
    }));
    await page.goto('https://mori-update-fixture.invalid/page');
    const tabId = () => worker.evaluate(async () => (await chrome.tabs.query({url: 'https://mori-update-fixture.invalid/*'}))[0].id);
    const message = async payload => worker.evaluate(({id, payload}) => chrome.tabs.sendMessage(id, payload), {id: await tabId(), payload});
    const ready = async version => until(async () => {
      const response = await message({action: 'mori-collector-ping'});
      return response.ready && response.version === version;
    }, 'collector ready at ' + version);
    const poll = () => worker.evaluate(() => updater.poll());
    const startDrag = async () => {
      await page.mouse.move(70, 70);await page.mouse.down();await page.mouse.move(100, 105, {steps: 7});
      await page.waitForSelector('[data-mori-collector]');
    };
    const cancelDrag = async () => {
      await page.keyboard.press('Escape');await page.mouse.up();
      await page.waitForSelector('[data-mori-collector]', {state: 'detached'});
    };
    const drop = async () => {await page.mouse.move(260, 220, {steps: 8});await page.mouse.up();};
    await ready(originalManifest.version);
    await page.evaluate(() => {window.fixtureDocumentMarker = 'original-open-page';});

    // A visible drag is live work, even though no background save has started.
    await startDrag();state.offer = true;
    const dragging = await poll();
    assert.equal(dragging.reason, 'deferred');assert.equal(state.applies.length, 0);
    assert.equal(await page.locator('[data-mori-collector]').count(), 1);
    state.offer = false;await cancelDrag();
    console.log('PASS active drag defers update and keeps the collector');

    // Exercise the real editor directly, without relying on a test-only shadow
    // root or modifying the production content script's closed-DOM behavior.
    assert.equal((await message({action: 'context-edit', requestId: 'fixture-editor',
      item: {url: image, source: page.url(), name: 'Unsaved editor text', kind: 'image'}})).displayed, true);
    await page.keyboard.type('User draft still editing');
    state.offer = true;const editing = await poll();
    assert.equal(editing.reason, 'deferred');assert.equal(state.applies.length, 0);
    assert.equal(await page.locator('[data-mori-collector]').count(), 1);assert.equal(state.saves.length, 0);
    state.offer = false;await page.keyboard.press('Escape');
    await page.waitForSelector('[data-mori-collector]', {state: 'detached'});
    console.log('PASS unsaved editor defers update without saving or dismissing it');

    // A quick-save collector disappears before the response arrives. That is
    // still busy and must never be mistaken for an idle update opportunity.
    state.holdSave = true;await startDrag();await drop();
    await until(() => state.held, 'synthetic in-flight save');
    await page.waitForSelector('[data-mori-collector]', {state: 'detached'});
    state.offer = true;const saving = await poll();
    assert.equal(saving.reason, 'busy');assert.equal(state.applies.length, 0);
    state.offer = false;state.holdSave = false;state.held();state.held = null;
    await until(async () => !(await worker.evaluate(() => updater.status())).busy, 'save finished');
    console.log('PASS detached background save blocks reload until its response arrives');

    // Actual manifest-version update and runtime reload, with no page refresh.
    const beforeNavigations = navigations;
    const nextWorker = browser.waitForEvent('serviceworker', {timeout: 15000});
    state.offer = true;
    await poll().catch(error => {
      if (!/closed|destroyed|Target|Execution context/i.test(error.message)) throw error;
    });
    worker = await nextWorker;
    assert.equal(new URL(worker.url()).host, id, 'update keeps the extension identity');
    await ready(nextVersion);
    assert.equal(state.applies.length, 1, 'idle update applies exactly once');
    assert.equal(navigations, beforeNavigations, 'original page was not navigated or refreshed');
    assert.equal(await page.evaluate(() => window.fixtureDocumentMarker), 'original-open-page');
    assert.deepEqual(await worker.evaluate(() => MoriPreferences.read()), preferences, 'all original preferences survive reload');
    assert.equal(await page.locator('[data-mori-collector]').count(), 0);
    console.log('PASS idle update reloads the real worker, preserves identity/preferences and repairs the open page');

    const beforeSaves = state.saves.length;
    await startDrag();assert.equal(await page.locator('[data-mori-collector]').count(), 1);
    await drop();await until(() => state.saves.length === beforeSaves + 1, 'post-update drag save');
    await until(async () => !(await worker.evaluate(() => updater.status())).busy, 'post-update save finished');
    await pause(250);
    assert.equal(state.saves.length, beforeSaves + 1, 'repaired page has exactly one save handler');
    assert.equal(state.saves.at(-1).name, 'Synthetic update image');
    assert.equal(state.applies.length, 1, 'no reload loop after successful update');
    assert.deepEqual(errors, []);if (state.error) throw state.error;
    console.log('PASS one post-update drag saves once; no duplicate handler or reload loop');
    console.log('PASS real isolated Edge MV3 update integration; fixture: ' + temp);
  } finally {
    if (state.held) {state.held();state.held = null;}
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => {console.error(error);process.exitCode = 1;});
