const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "..");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const source = fs.readFileSync(path.join(root, "src", "extension.ts"), "utf8");

function minimumVersion(range) {
    const match = /(\d+)\.(\d+)/.exec(range);
    assert.ok(match, `unparseable range: ${range}`);
    return [Number(match[1]), Number(match[2])];
}

test("engines.vscode is not older than the @types/vscode API it compiles against", () => {
    const [engineMajor, engineMinor] = minimumVersion(manifest.engines.vscode);
    const [typesMajor, typesMinor] = minimumVersion(manifest.devDependencies["@types/vscode"]);
    assert.ok(engineMajor > typesMajor || (engineMajor === typesMajor && engineMinor >= typesMinor));
});

test("contributed commands and registered commands are the same set", () => {
    const contributed = manifest.contributes.commands.map((command) => command.command).sort();
    const registered = [...source.matchAll(/registerCommand\("([^"]+)"/g)].map((match) => match[1]).sort();
    assert.deepEqual(contributed, registered);
});

test("the packaged extension ships no runtime dependencies", () => {
    assert.equal(manifest.dependencies, undefined);
});
