import test from 'node:test';
import assert from 'node:assert/strict';
import dgram from 'node:dgram';
import { once } from 'node:events';
import { XPlaneUdpBridge } from '../src/xplane/udpBridge.js';

function positionPacket(latitude, longitude, altitude) {
  const buffer = Buffer.alloc(41);
  buffer.write('DATA\0', 0, 'ascii');
  buffer.writeInt32LE(20, 5);
  [latitude, longitude, altitude, altitude - 200, 0, 0, 0, 0]
    .forEach((value, index) => buffer.writeFloatLE(value, 9 + index * 4));
  return buffer;
}

test('receives a UDP DATA packet and emits telemetry', async (context) => {
  const bridge = new XPlaneUdpBridge({ host: '127.0.0.1', port: 0 });
  const sender = dgram.createSocket('udp4');
  context.after(() => { sender.close(); bridge.stop(); });
  bridge.start();
  const [address] = await once(bridge, 'listening');
  const telemetryPromise = once(bridge, 'telemetry');
  sender.send(positionPacket(22.308, 113.9185, 3000), address.port, '127.0.0.1');
  const [telemetry] = await telemetryPromise;
  assert.ok(Math.abs(telemetry.latitude - 22.308) < 0.0001);
  assert.ok(Math.abs(telemetry.longitude - 113.9185) < 0.0001);
  assert.equal(telemetry.altitudeMslFt, 3000);
  assert.equal(bridge.stats.parsed, 1);
});
