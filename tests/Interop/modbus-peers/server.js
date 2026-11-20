// Independent Modbus TCP server for the interop tests: node server.js <modbus-serial|jsmodbus> <port>
// Prints {"ready":true,...} once listening; exits when stdin closes.
// Data (addresses 0..999 in every table, the same in both libraries):
//   coils[i] = i % 3 == 0, discrete inputs[i] = i % 2 == 1,
//   holding registers[i] = i * 3, input registers[i] = 1000 + i.
// Unit ID 1 only. Addresses from 1000 on do not exist (exception 02).
"use strict";
const net = require("net");

const [lib, portArg] = process.argv.slice(2);
const port = Number(portArg);
const SIZE = 1000;

const coils = Array.from({ length: SIZE }, (_, i) => i % 3 === 0);
const discrete = Array.from({ length: SIZE }, (_, i) => i % 2 === 1);
const holding = Array.from({ length: SIZE }, (_, i) => (i * 3) & 0xffff);
const input = Array.from({ length: SIZE }, (_, i) => 1000 + i);

function ready(extra) {
  process.stdout.write(JSON.stringify({ ready: true, lib, port, ...extra }) + "\n");
}

process.stdin.resume();
process.stdin.on("end", () => process.exit(0));
process.stdin.on("close", () => process.exit(0));

if (lib === "modbus-serial") {
  const ModbusRTU = require("modbus-serial");
  const illegal = { modbusErrorCode: 0x02, msg: "Illegal data address" };
  const get = (table) => (addr, unitID, cb) => (addr < SIZE ? cb(null, table[addr]) : cb(illegal));
  const vector = {
    getCoil: get(coils),
    getDiscreteInput: get(discrete),
    getHoldingRegister: get(holding),
    getInputRegister: get(input),
    setCoil: (addr, value, unitID, cb) => {
      if (addr >= SIZE) return cb(illegal);
      coils[addr] = !!value;
      cb();
    },
    setRegister: (addr, value, unitID, cb) => {
      if (addr >= SIZE) return cb(illegal);
      holding[addr] = value;
      cb();
    },
  };
  const server = new ModbusRTU.ServerTCP(vector, { host: "127.0.0.1", port, unitID: 1 });
  server.on("initialized", () => ready());
  server.on("socketError", () => {});
  server.on("serverError", (err) => process.stderr.write(String(err) + "\n"));
} else if (lib === "jsmodbus") {
  const Modbus = require("jsmodbus");
  const holdingBuffer = Buffer.alloc(SIZE * 2);
  const inputBuffer = Buffer.alloc(SIZE * 2);
  const coilBuffer = Buffer.alloc(SIZE / 8);
  const discreteBuffer = Buffer.alloc(SIZE / 8);
  for (let i = 0; i < SIZE; i++) {
    holdingBuffer.writeUInt16BE(holding[i], i * 2);
    inputBuffer.writeUInt16BE(input[i], i * 2);
    if (coils[i]) coilBuffer[i >> 3] |= 1 << (i & 7);
    if (discrete[i]) discreteBuffer[i >> 3] |= 1 << (i & 7);
  }
  const netServer = new net.Server();
  // A client that resets its connection must not take the whole server down.
  netServer.on("connection", (socket) => socket.on("error", () => {}));
  // jsmodbus answers every unit ID.
  new Modbus.server.TCP(netServer, { holding: holdingBuffer, input: inputBuffer, coils: coilBuffer, discrete: discreteBuffer });
  netServer.listen(port, "127.0.0.1", () => ready());
} else {
  process.stderr.write("usage: node server.js <modbus-serial|jsmodbus> <port>\n");
  process.exit(2);
}
