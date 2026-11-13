#!/usr/bin/env node
"use strict";
// Builds the UPM tarball of a release, com.scenario.unity-X.Y.Z.tgz: the
// Packages/com.scenario.unity folder laid out as `npm pack` lays it out (every
// file under package/), which the Package Manager installs with "Install
// package from tarball". It packs the package's files as committed at a git
// revision (HEAD by default), never the working tree, so an untracked or
// edited file cannot ship. The archive is reproducible: entries in path
// order, mode 0644, owner 0, npm pack's fixed date, gzip with no name or time.
//
//   node tools/release/build-package.js [--ref <revision>] [--out <dir>]
//
// Writes <dir>/com.scenario.unity-<version>.tgz (dist/ by default) and prints
// its path. tools/release/check-package.js checks the result.

const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");
const { execFileSync } = require("node:child_process");

const REPO = path.resolve(__dirname, "..", "..");
const PACKAGE_DIR = "Packages/com.scenario.unity";
const PACKAGE_NAME = "com.scenario.unity";
// 1985-10-26T08:15:00Z, the date npm pack gives every entry.
const MTIME = 499162500;
const BLOCK = 512;

function git(repo, args, input) {
  return execFileSync("git", args, { cwd: repo, input, maxBuffer: 1 << 30, stdio: ["pipe", "pipe", "pipe"] });
}

// The package's committed files at `ref`: [{ path, data }] in path order,
// `path` relative to the package folder. Anything but a regular file (a
// symbolic link, a submodule, an executable) is refused.
function packageFiles(repo = REPO, ref = "HEAD") {
  const listing = git(repo, ["ls-tree", "-r", "-z", ref, "--", `${PACKAGE_DIR}/`]).toString("utf8");
  const rows = listing.split("\0").filter(Boolean).map((row) => {
    const tab = row.indexOf("\t");
    const [mode, type, sha] = row.slice(0, tab).split(" ");
    return { mode, type, sha, path: row.slice(tab + 1) };
  });
  if (!rows.length) throw new Error(`${PACKAGE_DIR} has no files at ${ref}`);
  for (const row of rows) {
    if (row.type !== "blob" || row.mode !== "100644") throw new Error(`${row.path}: ${row.type} ${row.mode} is not a regular file`);
  }
  // One git process reads every blob: "<sha> blob <size>\n<bytes>\n" each.
  const out = git(repo, ["cat-file", "--batch"], rows.map((r) => r.sha).join("\n") + "\n");
  let at = 0;
  const files = rows.map((row) => {
    const eol = out.indexOf(0x0a, at);
    const [sha, type, size] = out.toString("utf8", at, eol).split(" ");
    if (sha !== row.sha || type !== "blob") throw new Error(`${row.path}: git cat-file answered ${sha} ${type}`);
    const start = eol + 1;
    const data = out.subarray(start, start + Number(size));
    at = start + Number(size) + 1;
    return { path: row.path.slice(PACKAGE_DIR.length + 1), data };
  });
  return files.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
}

function octal(value, width) {
  return value.toString(8).padStart(width - 1, "0") + "\0";
}

// A ustar header for a regular file.
function header(name, size) {
  if (Buffer.byteLength(name, "utf8") > 100) throw new Error(`${name}: longer than the 100 bytes a ustar name holds`);
  const h = Buffer.alloc(BLOCK);
  h.write(name, 0, 100, "utf8");
  h.write(octal(0o644, 8), 100, "ascii");
  h.write(octal(0, 8), 108, "ascii");
  h.write(octal(0, 8), 116, "ascii");
  h.write(octal(size, 12), 124, "ascii");
  h.write(octal(MTIME, 12), 136, "ascii");
  h.fill(0x20, 148, 156);
  h.write("0", 156, "ascii");
  h.write("ustar\0", 257, "ascii");
  h.write("00", 263, "ascii");
  let sum = 0;
  for (const byte of h) sum += byte;
  h.write(sum.toString(8).padStart(6, "0") + "\0 ", 148, "ascii");
  return h;
}

// A gzipped ustar archive of `files` ([{ path, data }]) under package/.
function tarball(files) {
  const parts = [];
  for (const file of files) {
    parts.push(header(`package/${file.path}`, file.data.length), file.data);
    const pad = (BLOCK - (file.data.length % BLOCK)) % BLOCK;
    if (pad) parts.push(Buffer.alloc(pad));
  }
  parts.push(Buffer.alloc(BLOCK * 2));
  const gz = zlib.gzipSync(Buffer.concat(parts), { level: 9 });
  // The gzip header names the operating system that wrote it; 255 is "unknown",
  // so the same files give the same bytes on any runner.
  gz[9] = 255;
  return gz;
}

function packageVersion(files) {
  const manifest = files.find((f) => f.path === "package.json");
  if (!manifest) throw new Error(`${PACKAGE_DIR}/package.json is missing`);
  const { name, version } = JSON.parse(manifest.data.toString("utf8"));
  if (name !== PACKAGE_NAME) throw new Error(`package name is ${name}, expected ${PACKAGE_NAME}`);
  if (!/^\d+\.\d+\.\d+$/.test(version || "")) throw new Error(`package version ${version} is not X.Y.Z`);
  return version;
}

// Builds the tarball of `ref` into `outDir`; returns its path.
function build({ repo = REPO, ref = "HEAD", outDir = path.join(REPO, "dist") } = {}) {
  const files = packageFiles(repo, ref);
  const version = packageVersion(files);
  fs.mkdirSync(outDir, { recursive: true });
  const file = path.join(outDir, `${PACKAGE_NAME}-${version}.tgz`);
  fs.writeFileSync(file, tarball(files));
  return file;
}

function parseArgs(argv) {
  const options = {};
  for (let i = 0; i < argv.length; i++) {
    const [flag, value] = [argv[i], argv[i + 1]];
    if ((flag === "--ref" || flag === "--out") && value) {
      options[flag === "--ref" ? "ref" : "outDir"] = flag === "--out" ? path.resolve(value) : value;
      i++;
    } else {
      throw new Error(`unknown argument ${flag}`);
    }
  }
  return options;
}

if (require.main === module) {
  try {
    process.stdout.write(`${build(parseArgs(process.argv.slice(2)))}\n`);
  } catch (e) {
    process.stderr.write(`build-package: ${e.message}\n`);
    process.stderr.write("usage: node tools/release/build-package.js [--ref <revision>] [--out <dir>]\n");
    process.exit(1);
  }
}

module.exports = { PACKAGE_DIR, PACKAGE_NAME, MTIME, packageFiles, packageVersion, tarball, build };
