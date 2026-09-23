import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { listManuals, manualFile, parseRange, resolveInside } from '../src/manuals.js';

function withFolder(run) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'efb-manuals-'));
  try {
    run(root);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

test('lists PDFs recursively and ignores everything else', () => {
  withFolder((root) => {
    fs.writeFileSync(path.join(root, 'A320 手册.pdf'), 'x');
    fs.writeFileSync(path.join(root, 'notes.txt'), 'x');
    fs.mkdirSync(path.join(root, '波音'));
    fs.writeFileSync(path.join(root, '波音', 'B737.pdf'), 'x');
    const listing = listManuals(root);
    assert.equal(listing.configured, true);
    assert.deepEqual(listing.files.map((file) => file.path).sort(), ['A320 手册.pdf', '波音/B737.pdf']);
    assert.equal(listing.files.every((file) => file.size >= 1), true);
  });
});

test('an unset folder is reported as not configured', () => {
  assert.equal(listManuals('').configured, false);
  assert.equal(listManuals('').files.length, 0);
  assert.equal(listManuals(undefined).configured, false);
});

test('a missing folder reports an error instead of throwing', () => {
  const listing = listManuals(path.join(os.tmpdir(), 'efb-does-not-exist-12345'));
  assert.equal(listing.configured, false);
  assert.match(listing.error, /找不到/);
});

test('serves a PDF from inside the folder', () => {
  withFolder((root) => {
    fs.mkdirSync(path.join(root, 'sub'));
    fs.writeFileSync(path.join(root, 'sub', 'manual.pdf'), 'hello');
    const found = manualFile(root, 'sub/manual.pdf');
    assert.equal(found.size, 5);
    assert.equal(path.basename(found.absolute), 'manual.pdf');
  });
});

test('refuses traversal, absolute paths and non-PDFs', () => {
  withFolder((root) => {
    fs.writeFileSync(path.join(root, 'manual.pdf'), 'x');
    fs.writeFileSync(path.join(root, 'secret.txt'), 'x');
    assert.equal(manualFile(root, '../secret.txt'), null);
    assert.equal(manualFile(root, '..\\secret.txt'), null);
    assert.equal(manualFile(root, 'sub/../../outside.pdf'), null);
    assert.equal(manualFile(root, 'C:/Windows/system32/win.ini'), null);
    assert.equal(manualFile(root, 'secret.txt'), null);
    assert.equal(manualFile(root, ''), null);
    assert.equal(manualFile('', 'manual.pdf'), null);
    assert.equal(resolveInside(root, 'a\0b.pdf'), null);
  });
});

test('parses the byte ranges PDF.js sends', () => {
  assert.deepEqual(parseRange('bytes=0-99', 1000), { start: 0, end: 99, length: 100 });
  assert.deepEqual(parseRange('bytes=100-', 1000), { start: 100, end: 999, length: 900 });
  assert.deepEqual(parseRange('bytes=-100', 1000), { start: 900, end: 999, length: 100 });
  // Clamped rather than rejected: browsers do ask past the end.
  assert.deepEqual(parseRange('bytes=900-5000', 1000), { start: 900, end: 999, length: 100 });
  assert.equal(parseRange('bytes=1000-1200', 1000), null);
  assert.equal(parseRange('items=0-10', 1000), null);
  assert.equal(parseRange('bytes=abc-def', 1000), null);
  assert.equal(parseRange('', 1000), null);
  assert.equal(parseRange('bytes=0-10', 0), null);
});

test('the listing is capped', () => {
  withFolder((root) => {
    for (let index = 0; index < 8; index += 1) fs.writeFileSync(path.join(root, `m${index}.pdf`), 'x');
    const listing = listManuals(root, { limit: 5 });
    assert.equal(listing.files.length, 5);
    assert.equal(listing.truncated, true);
  });
});
