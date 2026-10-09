const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const createImporter = require('../web/model/occt/occt-import-js.js');
(async () => {
  const importer = await createImporter();
  const result = importer.ReadStepFile(fs.readFileSync(path.join(__dirname, 'cube.step')), null);
  assert.equal(result.success, true);
  assert.ok(result.meshes.length > 0);
  assert.ok(result.meshes.every(mesh => mesh.attributes.position.array.length > 0));
  console.log('PASS bundled offline STEP engine: ' + result.meshes.length + ' mesh(es)');
})().catch(error => { console.error(error); process.exitCode = 1; });
