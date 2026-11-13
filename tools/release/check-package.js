#!/usr/bin/env node
"use strict";
// Checks a release tarball before it is attached and published: it must be
// exactly the package of this checkout, installable by the Package Manager.
// No runner has Unity, so this reads the .tgz instead.
//
//   node tools/release/check-package.js dist/com.scenario.unity-X.Y.Z.tgz X.Y.Z [--ref <revision>]
//
// - The file is named com.scenario.unity-X.Y.Z.tgz, a gzipped ustar archive of
//   regular files, every one under package/ (npm pack's layout).
// - Its package/package.json is com.scenario.unity at version X.Y.Z, the
//   version of the release tag.
// - Its entries are the files of Packages/com.scenario.unity committed at the
//   revision (HEAD by default), byte for byte: nothing missing, nothing extra.
// - Every asset and folder Unity imports has its .meta, and every .meta its
//   asset, so the package imports with the GUIDs it was built with.
// - Nothing dev-only: no editor, OS or build leftovers, nothing from
//   Assets/ScenarioDev, no secrets file.

const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");
const { PACKAGE_NAME, packageFiles } = require("./build-package.js");

const BLOCK = 512;
// Path segments that only belong to a development checkout.
const DEV_ONLY = /^(\.DS_Store|Thumbs\.db|desktop\.ini|\.git|\.vs|\.idea|\.vscode|node_modules|Library|Temp|Logs|obj|UserSettings|ScenarioDev|AgentKit|\.env(\..*)?|.*\.(csproj|sln|slnx|pdb|mdb|tmp|orig|rej|swp|ulf|pem|key))$/i;

function field(h, start, length) {
  const end = h.indexOf(0, start);
  return h.toString("utf8", start, end === -1 || end > start + length ? start + length : end);
}

// The entries of a gzipped tar archive: [{ name, type, data }]. Throws on a
// damaged archive.
function readTarball(buf) {
  const tar = zlib.gunzipSync(buf);
  const entries = [];
  let at = 0;
  while (at + BLOCK <= tar.length) {
    const h = tar.subarray(at, at + BLOCK);
    if (h.every((b) => b === 0)) return entries;
    let sum = 0;
    for (let i = 0; i < BLOCK; i++) sum += i >= 148 && i < 156 ? 0x20 : h[i];
    if (parseInt(field(h, 148, 8).trim(), 8) !== sum) throw new Error(`entry ${entries.length + 1}: header checksum does not match`);
    const prefix = field(h, 257, 6) === "ustar" ? field(h, 345, 155) : "";
    const name = (prefix ? `${prefix}/` : "") + field(h, 0, 100);
    // Octal digits only: a sign or anything else would make a negative or
    // NaN size, and the next header would be read from the same place.
    const sizeField = field(h, 124, 12).trim();
    if (!/^[0-7]+$/.test(sizeField)) throw new Error(`${name}: size field ${JSON.stringify(sizeField)} is not an octal number`);
    const size = parseInt(sizeField, 8);
    const type = String.fromCharCode(h[156] || 0x30);
    const start = at + BLOCK;
    if (start + size > tar.length) throw new Error(`${name}: truncated`);
    entries.push({ name, type, data: tar.subarray(start, start + size) });
    at = start + Math.ceil(size / BLOCK) * BLOCK;
  }
  throw new Error("no end-of-archive marker");
}

// Unity skips hidden files and folders and those whose name ends with "~"
// (Documentation~): they get no .meta.
function ignoredByUnity(rel) {
  return rel.split("/").some((part) => part.startsWith(".") || part.endsWith("~"));
}

// .meta problems among package-relative file paths.
function metaProblems(paths) {
  const problems = [];
  const files = new Set(paths);
  const folders = new Set();
  for (const p of paths) {
    const parts = p.split("/");
    for (let i = 1; i < parts.length; i++) folders.add(parts.slice(0, i).join("/"));
  }
  for (const p of [...paths, ...folders].sort()) {
    if (ignoredByUnity(p)) continue;
    if (p.endsWith(".meta")) {
      const asset = p.slice(0, -".meta".length);
      if (!files.has(asset) && !folders.has(asset)) problems.push(`${p} has no asset`);
    } else if (!files.has(`${p}.meta`)) {
      problems.push(`${p} has no .meta`);
    }
  }
  return problems;
}

// The problems found in the tarball at `file` for `version`; empty when it is
// the package of `ref` in `repo`.
function checkPackage(file, version, { repo, ref = "HEAD" } = {}) {
  const problems = [];
  const expected = `${PACKAGE_NAME}-${version}.tgz`;
  if (path.basename(file) !== expected) problems.push(`${path.basename(file)}: expected ${expected}`);
  let entries;
  try {
    entries = readTarball(fs.readFileSync(file));
  } catch (e) {
    return [...problems, `${path.basename(file)}: ${e.message}`];
  }
  const byPath = new Map();
  for (const entry of entries) {
    if (entry.type !== "0") problems.push(`${entry.name}: entry type ${entry.type}, expected a regular file`);
    if (!entry.name.startsWith("package/")) problems.push(`${entry.name}: outside package/`);
    const rel = entry.name.slice("package/".length);
    if (!rel || rel.split("/").some((part) => part === "" || part === "." || part === "..")) problems.push(`${entry.name}: not a plain relative path`);
    if (byPath.has(rel)) problems.push(`${entry.name}: appears twice`);
    byPath.set(rel, entry.data);
    for (const part of rel.split("/")) {
      if (DEV_ONLY.test(part)) problems.push(`${entry.name}: dev-only content (${part})`);
    }
  }

  const manifest = byPath.get("package.json");
  if (!manifest) {
    problems.push("package/package.json is missing");
  } else {
    try {
      const m = JSON.parse(manifest.toString("utf8"));
      if (m.name !== PACKAGE_NAME) problems.push(`package name is ${m.name}, expected ${PACKAGE_NAME}`);
      if (m.version !== version) problems.push(`package version is ${m.version}, expected ${version}`);
    } catch (e) {
      problems.push(`package/package.json: ${e.message}`);
    }
  }

  problems.push(...metaProblems([...byPath.keys()]));

  const committed = new Map(packageFiles(repo, ref).map((f) => [f.path, f.data]));
  for (const [rel, data] of committed) {
    if (!byPath.has(rel)) problems.push(`package/${rel} is missing (committed at ${ref})`);
    else if (!byPath.get(rel).equals(data)) problems.push(`package/${rel} differs from the committed file`);
  }
  for (const rel of byPath.keys()) {
    if (!committed.has(rel)) problems.push(`package/${rel} is not a committed package file`);
  }
  return problems;
}

if (require.main === module) {
  const args = process.argv.slice(2);
  const refAt = args.indexOf("--ref");
  const ref = refAt === -1 ? "HEAD" : args.splice(refAt, 2)[1];
  const [file, version] = args;
  if (!file || !/^\d+\.\d+\.\d+$/.test(version || "") || !ref || args.length !== 2) {
    process.stderr.write("usage: node tools/release/check-package.js <com.scenario.unity-X.Y.Z.tgz> <X.Y.Z> [--ref <revision>]\n");
    process.exit(2);
  }
  const problems = checkPackage(path.resolve(file), version, { ref });
  for (const p of problems) process.stderr.write(`::error::${p}\n`);
  if (!problems.length) {
    const count = readTarball(fs.readFileSync(file)).length;
    process.stdout.write(`${path.basename(file)}: the package committed at ${ref}, ${count} files, every .meta present\n`);
  }
  process.exit(problems.length ? 1 : 0);
}

module.exports = { checkPackage, readTarball, metaProblems, ignoredByUnity, DEV_ONLY };
