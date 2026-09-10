import test from 'node:test';
import assert from 'node:assert/strict';
import { parseDataPacket, parsePacket } from '../src/xplane/parser.js';

function packet(rows) {
  const buffer = Buffer.alloc(5 + rows.length * 36);
  buffer.write('DATA\0', 0, 'ascii');
  rows.forEach(([index, values], row) => {
    const offset = 5 + row * 36;
    buffer.writeInt32LE(index, offset);
    values.forEach((value, i) => buffer.writeFloatLE(value, offset + 4 + i * 4));
  });
  return buffer;
}

test('parses DATA rows and maps flight telemetry', () => {
  const input = packet([
    [3, [125, 123, 130, 121, 0, 0, 0, 0]],
    [4, [0.42, 850, 845, 0, 1.02, 0, 0, 0]],
    [17, [2.5, -4, 270, 268, 0, 0, 0, 0]],
    [20, [31.1434, 121.8052, 5000, 4800, 0, 0, 0, 0]]
  ]);
  const parsed = parsePacket(input);
  assert.equal(parsed.protocol, 'DATA');
  assert.ok(Math.abs(parsed.fields.latitude - 31.1434) < 0.0001);
  assert.equal(parsed.fields.headingTrueDeg, 270);
  assert.equal(parsed.fields.indicatedAirspeedKt, 125);
  assert.equal(parsed.fields.verticalSpeedFpm, 850);
});

test('rejects unrelated and truncated packets', () => {
  assert.equal(parseDataPacket(Buffer.from('hello')), null);
  assert.equal(parseDataPacket(Buffer.from('DATA\0short')), null);
});
