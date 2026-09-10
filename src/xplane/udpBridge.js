import dgram from 'node:dgram';
import { EventEmitter } from 'node:events';
import { parsePacket } from './parser.js';

export class XPlaneUdpBridge extends EventEmitter {
  constructor({ host, port, sourceIp = '' }) {
    super();
    this.options = { host, port, sourceIp };
    this.socket = dgram.createSocket({ type: 'udp4', reuseAddr: true });
    this.state = {};
    this.stats = { packets: 0, parsed: 0, rejected: 0, lastPacketAt: null, lastSender: null };
  }

  start() {
    this.socket.on('error', (error) => this.emit('error', error));
    this.socket.on('message', (message, remote) => {
      this.stats.packets += 1;
      this.stats.lastPacketAt = new Date().toISOString();
      this.stats.lastSender = `${remote.address}:${remote.port}`;
      if (this.options.sourceIp && remote.address !== this.options.sourceIp) {
        this.stats.rejected += 1;
        return;
      }
      const parsed = parsePacket(message);
      if (!parsed) return;
      this.stats.parsed += 1;
      this.state = {
        ...this.state,
        ...parsed.fields,
        source: remote.address,
        protocol: parsed.protocol,
        receivedAt: Date.now()
      };
      this.emit('telemetry', this.state);
    });
    this.socket.bind(this.options.port, this.options.host, () => this.emit('listening', this.socket.address()));
  }

  stop() { this.socket.close(); }
}
