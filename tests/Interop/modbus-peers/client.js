// Independent Modbus TCP client for the interop tests: node client.js <modbus-serial|jsmodbus> <port>
// Runs a fixed sequence against our server (unit 1, 1000 addresses per table, see the C# test),
// then prints one JSON line with every step's outcome: {"step": {"data": ...}} or {"step": {"exception": code}}.
"use strict";
const net = require("net");

const [lib, portArg] = process.argv.slice(2);
const port = Number(portArg);
const results = {};

async function step(name, fn) {
  if (process.env.MB_DEBUG) process.stderr.write("step " + name + "\n");
  try {
    results[name] = { data: await fn() };
  } catch (err) {
    const code =
      err && err.modbusCode !== undefined ? err.modbusCode :
      err && err.response && err.response._body && err.response._body.code !== undefined ? err.response._body.code :
      null;
    results[name] = code !== null ? { exception: code } : { error: String((err && err.message) || err) };
  }
}

async function withModbusSerial() {
  const ModbusRTU = require("modbus-serial");
  const client = new ModbusRTU();
  await client.connectTCP("127.0.0.1", { port });
  client.setTimeout(3000);
  client.setID(1);
  await step("readCoils", async () => (await client.readCoils(0, 10)).data.slice(0, 10));
  await step("readDiscreteInputs", async () => (await client.readDiscreteInputs(0, 10)).data.slice(0, 10));
  await step("readHoldingRegisters", async () => (await client.readHoldingRegisters(10, 5)).data);
  await step("readInputRegisters", async () => (await client.readInputRegisters(990, 10)).data);
  await step("readMaxRegisters", async () => (await client.readHoldingRegisters(0, 125)).data.length);
  await step("readMaxCoils", async () => (await client.readCoils(0, 2000)).data.filter(Boolean).length);
  await step("writeCoil", async () => (await client.writeCoil(500, true), true));
  await step("writeRegister", async () => (await client.writeRegister(501, 0xbeef), true));
  await step("writeCoils", async () => (await client.writeCoils(510, [true, false, true, true, false, true, false, true, true]), true));
  await step("writeRegisters", async () => (await client.writeRegisters(520, [1, 2, 3]), true));
  await step("readWriteRegisters", async () => (await client.readWriteRegisters(520, 3, 530, 2, [7, 8])).data);
  await step("readPastTheEnd", async () => (await client.readHoldingRegisters(998, 5)).data);
  client.setID(9);
  await step("unknownUnit", async () => (await client.readHoldingRegisters(0, 1)).data);
  client.close(() => {});
}

async function withJsModbus() {
  const Modbus = require("jsmodbus");
  const socket = new net.Socket();
  const client = new Modbus.client.TCP(socket, 1, 3000);
  await new Promise((resolve, reject) => {
    socket.once("connect", resolve);
    socket.once("error", reject);
    socket.connect({ host: "127.0.0.1", port });
  });
  const body = (r) => r.response._body;
  await step("readCoils", async () => body(await client.readCoils(0, 10)).valuesAsArray.slice(0, 10).map(Boolean));
  await step("readDiscreteInputs", async () => body(await client.readDiscreteInputs(0, 10)).valuesAsArray.slice(0, 10).map(Boolean));
  await step("readHoldingRegisters", async () => Array.from(body(await client.readHoldingRegisters(10, 5)).valuesAsArray));
  await step("readInputRegisters", async () => Array.from(body(await client.readInputRegisters(990, 10)).valuesAsArray));
  await step("readMaxRegisters", async () => body(await client.readHoldingRegisters(0, 125)).valuesAsArray.length);
  await step("readMaxCoils", async () => body(await client.readCoils(0, 2000)).valuesAsArray.slice(0, 2000).filter(Boolean).length);
  await step("writeCoil", async () => (await client.writeSingleCoil(500, true), true));
  await step("writeRegister", async () => (await client.writeSingleRegister(501, 0xbeef), true));
  await step("writeCoils", async () => (await client.writeMultipleCoils(510, [true, false, true, true, false, true, false, true, true]), true));
  await step("writeRegisters", async () => (await client.writeMultipleRegisters(520, [1, 2, 3]), true));
  await step("readPastTheEnd", async () => Array.from(body(await client.readHoldingRegisters(998, 5)).valuesAsArray));
  socket.end();
}

const run = lib === "modbus-serial" ? withModbusSerial : lib === "jsmodbus" ? withJsModbus : null;
if (!run) {
  process.stderr.write("usage: node client.js <modbus-serial|jsmodbus> <port>\n");
  process.exit(2);
}
run()
  .then(() => {
    process.stdout.write(JSON.stringify(results) + "\n");
    process.exit(0);
  })
  .catch((err) => {
    process.stderr.write(String((err && err.stack) || err) + "\n");
    process.exit(1);
  });
