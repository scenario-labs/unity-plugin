"use strict";
// The release tarball (tools/release/build-package.js) and its check
// (tools/release/check-package.js), against this checkout's committed package.
//
//   node --test "tests/tools/*.test.js"

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { MTIME, packageFiles, packageVersion, tarball, build } = require("../../tools/release/build-package.js");
const { checkPackage, readTarball, metaProblems } = require("../../tools/release/check-package.js");

const files = packageFiles();
const version = packageVersion(files);
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "unity-package-test-"));
test.after(() => fs.rmSync(tmp, { recursive: true, force: true }));

// Writes `entries` as a tarball named for `v` in a fresh folder; returns its path.
let n = 0;
function write(entries, v = version) {
  const dir = path.join(tmp, String(n++));
  fs.mkdirSync(dir);
  const file = path.join(dir, `com.scenario.unity-${v}.tgz`);
  fs.writeFileSync(file, tarball(entries));
  return file;
}

test("the built tarball is the committed package under package/, and passes its check", () => {
  const file = build({ outDir: path.join(tmp, "build") });
  assert.equal(path.basename(file), `com.scenario.unity-${version}.tgz`);
  const entries = readTarball(fs.readFileSync(file));
  assert.deepEqual(entries.map((e) => e.name), files.map((f) => `package/${f.path}`));
  assert.ok(entries.every((e) => e.type === "0"));
  assert.ok(entries.some((e) => e.name === "package/package.json"));
  assert.ok(entries.some((e) => e.name === "package/Editor.meta"));
  assert.deepEqual(checkPackage(file, version), []);
});

test("two builds give the same bytes, dated as npm pack dates its entries", () => {
  const a = fs.readFileSync(build({ outDir: path.join(tmp, "a") }));
  const b = fs.readFileSync(build({ outDir: path.join(tmp, "b") }));
  assert.ok(a.equals(b));
  assert.equal(a[9], 255);
  const header = require("node:zlib").gunzipSync(a).subarray(0, 512);
  assert.equal(parseInt(header.toString("ascii", 136, 147), 8), MTIME);
  assert.equal(header.toString("ascii", 100, 107), "0000644");
});

test("a tarball for another version fails, by name and by package.json", () => {
  const problems = checkPackage(write(files, "9.9.9"), "9.9.9");
  assert.ok(problems.some((p) => p.includes(`package version is ${version}, expected 9.9.9`)), problems.join("\n"));
  const misnamed = path.join(tmp, "misnamed.tgz");
  fs.copyFileSync(write(files), misnamed);
  assert.ok(checkPackage(misnamed, version).some((p) => p.includes("expected com.scenario.unity-")));
});

test("a missing .meta, a missing file and an edited file fail", () => {
  const meta = files.find((f) => f.path.endsWith(".cs.meta"));
  const without = files.filter((f) => f !== meta);
  const problems = checkPackage(write(without), version);
  assert.ok(problems.includes(`${meta.path.slice(0, -5)} has no .meta`), problems.join("\n"));
  assert.ok(problems.includes(`package/${meta.path} is missing (committed at HEAD)`));

  const edited = files.map((f) => (f.path === "README.md" ? { path: f.path, data: Buffer.concat([f.data, Buffer.from("\n")]) } : f));
  assert.deepEqual(checkPackage(write(edited), version), ["package/README.md differs from the committed file"]);
});

test("dev-only and uncommitted files fail", () => {
  const extra = [
    ...files,
    { path: ".DS_Store", data: Buffer.from("x") },
    { path: "Editor/ScenarioDev/Probe.cs", data: Buffer.from("class Probe {}") },
    { path: "Editor/ScenarioDev/Probe.cs.meta", data: Buffer.from("guid: 0") },
    { path: "Editor/ScenarioDev.meta", data: Buffer.from("guid: 1") },
  ];
  const problems = checkPackage(write(extra), version);
  assert.ok(problems.includes("package/.DS_Store: dev-only content (.DS_Store)"), problems.join("\n"));
  assert.ok(problems.includes("package/Editor/ScenarioDev/Probe.cs: dev-only content (ScenarioDev)"));
  assert.ok(problems.includes("package/Editor/ScenarioDev/Probe.cs is not a committed package file"));
});

test("a damaged or foreign archive fails without throwing", () => {
  const file = write(files);
  const bytes = fs.readFileSync(file);
  fs.writeFileSync(file, bytes.subarray(0, bytes.length - 40));
  assert.match(checkPackage(file, version).join("\n"), /com\.scenario\.unity-.*\.tgz: /);
  assert.ok(checkPackage(write([{ path: "package.json", data: Buffer.from("{}") }]), version).some((p) => p.includes("is missing (committed at HEAD)")));
});

test(".meta rules follow what Unity imports", () => {
  assert.deepEqual(metaProblems(["package.json", "package.json.meta", "Documentation~/USER_GUIDE.md"]), []);
  assert.deepEqual(metaProblems(["Editor/A.cs", "Editor/A.cs.meta"]), ["Editor has no .meta"]);
  assert.deepEqual(metaProblems(["Editor.meta", "Editor/B.cs.meta"]), ["Editor/B.cs.meta has no asset"]);
});
