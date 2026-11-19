// Independent OPC UA server for the interop tests, built on node-opcua (MIT).
//
// Why: our client must work against OPC UA stacks we did not write. node-opcua is a separate,
// complete implementation (TypeScript), so a bug shared by our C# codec and open62541 would show
// up here. Never shipped; started by tests/Interop/Cictna.Industrial.OpcUa.InteropTests.
//
//   node server.js <port>
//
// Prints one JSON line {"ready":true,...} on stdout when listening, then runs until stdin closes
// or it receives SIGTERM. Address space (own namespace):
//   Types/<Type>            one writable variable per built-in type (values below)
//   Types/<Type>Array       a few arrays, Types/DoubleMatrix (2x3)
//   Many/Tag000..Tag249     Int32 variables whose value is their index
// Operation limits: 50 nodes per read/write, 10 per browse (to exercise batching).
"use strict";

const path = require("path");
const {
    OPCUAServer, OPCUACertificateManager, Variant, DataType, VariantArrayType,
    StatusCodes, MessageSecurityMode, SecurityPolicy, coerceNodeId,
} = require("node-opcua");

const port = parseInt(process.argv[2] || "48600", 10);

// Values. Keep identical to InteropValues.cs.
const time = new Date(Date.UTC(2026, 10, 12, 10, 30, 15, 123));
const scalars = [
    ["Boolean", DataType.Boolean, true],
    ["SByte", DataType.SByte, -5],
    ["Byte", DataType.Byte, 200],
    ["Int16", DataType.Int16, -1234],
    ["UInt16", DataType.UInt16, 54321],
    ["Int32", DataType.Int32, -123456],
    ["UInt32", DataType.UInt32, 4000000000],
    ["Int64", DataType.Int64, [0, 123456789]],   // node-opcua: [high, low]
    ["UInt64", DataType.UInt64, [1, 0]],          // 4294967296
    ["Float", DataType.Float, 3.25],
    ["Double", DataType.Double, Math.PI],
    ["String", DataType.String, "Pompe d'acide, خزان 2"],
    ["DateTime", DataType.DateTime, time],
    ["Guid", DataType.Guid, "09087E75-8E5E-499B-954F-F2A9603DB28A"],
    ["ByteString", DataType.ByteString, Buffer.from([0, 1, 2, 254, 255])],
    ["NodeId", DataType.NodeId, coerceNodeId("ns=1;s=Pump01")],
    ["StatusCode", DataType.StatusCode, StatusCodes.UncertainLastUsableValue],
    ["QualifiedName", DataType.QualifiedName, { namespaceIndex: 1, name: "Level" }],
    ["LocalizedText", DataType.LocalizedText, { locale: "fr", text: "Débit" }],
];
const arrays = [
    ["BooleanArray", DataType.Boolean, [true, false, true]],
    ["Int32Array", DataType.Int32, new Int32Array([1, 2, 3, 4])],
    ["DoubleArray", DataType.Double, new Float64Array([0.1, -1.5, 1e300])],
    ["StringArray", DataType.String, ["a", "", "عربي"]],
];

async function main() {
    const certificateManager = new OPCUACertificateManager({
        rootFolder: path.join(__dirname, "pki"),
        automaticallyAcceptUnknownCertificate: true, // test server: trust our client certificate
    });
    await certificateManager.initialize();

    const server = new OPCUAServer({
        port,
        resourcePath: "",
        buildInfo: { productName: "CictnaInteropServer", manufacturerName: "test" },
        serverCertificateManager: certificateManager,
        securityModes: [MessageSecurityMode.None, MessageSecurityMode.Sign, MessageSecurityMode.SignAndEncrypt],
        securityPolicies: [SecurityPolicy.None, SecurityPolicy.Basic256Sha256],
        allowAnonymous: true,
        serverCapabilities: {
            maxBrowseContinuationPoints: 10,
            operationLimits: { maxNodesPerRead: 50, maxNodesPerWrite: 50, maxNodesPerBrowse: 10, maxMonitoredItemsPerCall: 20 },
        },
    });
    await server.initialize();

    const addressSpace = server.engine.addressSpace;
    const ns = addressSpace.getOwnNamespace();
    const objects = addressSpace.rootFolder.objects;

    const types = ns.addFolder(objects, { browseName: "Types", nodeId: "s=Types" });
    const writable = { accessLevel: "CurrentRead | CurrentWrite", userAccessLevel: "CurrentRead | CurrentWrite" };
    for (const [name, dataType, value] of scalars) {
        ns.addVariable({
            componentOf: types, browseName: name, nodeId: `s=${name}`, dataType, valueRank: -1,
            value: new Variant({ dataType, arrayType: VariantArrayType.Scalar, value }), ...writable,
        });
    }
    for (const [name, dataType, value] of arrays) {
        ns.addVariable({
            componentOf: types, browseName: name, nodeId: `s=${name}`, dataType, valueRank: 1, arrayDimensions: [0],
            value: new Variant({ dataType, arrayType: VariantArrayType.Array, value }), ...writable,
        });
    }
    ns.addVariable({
        componentOf: types, browseName: "DoubleMatrix", nodeId: "s=DoubleMatrix", dataType: DataType.Double,
        valueRank: 2, arrayDimensions: [2, 3],
        value: new Variant({ dataType: DataType.Double, arrayType: VariantArrayType.Matrix, dimensions: [2, 3], value: new Float64Array([1, 2, 3, 4, 5, 6]) }),
        ...writable,
    });

    const many = ns.addFolder(objects, { browseName: "Many", nodeId: "s=Many" });
    for (let i = 0; i < 250; i++) {
        const tag = `Tag${String(i).padStart(3, "0")}`;
        ns.addVariable({
            componentOf: many, browseName: tag, nodeId: `s=Many.${tag}`, dataType: DataType.Int32,
            value: new Variant({ dataType: DataType.Int32, value: i }), ...writable,
        });
    }

    await server.start();
    console.log(JSON.stringify({ ready: true, port, namespaceUri: ns.namespaceUri, namespaceIndex: ns.index }));

    const stop = async () => {
        await server.shutdown(0);
        process.exit(0);
    };
    process.on("SIGTERM", stop);
    process.on("SIGINT", stop);
    process.stdin.on("end", stop); // parent test process exited
    process.stdin.resume();
}

main().catch((err) => {
    console.error(err);
    process.exit(1);
});
