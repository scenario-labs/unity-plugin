#!/usr/bin/env node
"use strict";
// Fills the page of unity.scenario.com with the release it describes, as
// photoshop.scenario.com and blender.scenario.com do: site/index.html says
// {{version}} where it names the release and {{ref}} in its links to the
// repository, and .github/workflows/pages.yml stamps them with the newest
// published release (its version and tag) when it publishes. The repository
// spells no version in the page, so a release never leaves it naming the one
// before. Adapted from scenario-labs/photoshop-plugin (MIT).
//
//   node tools/release/stamp-site.js <page.html> <version> <ref>

const fs = require("node:fs");

const VERSION = /^\d+\.\d+\.\d+$/;
const REF = /^(main|unity-plugin-v\d+\.\d+\.\d+)$/;

function stamp(html, version, ref) {
  if (!VERSION.test(version)) throw new Error(`Not a release version: ${JSON.stringify(version)}`);
  if (!REF.test(ref)) throw new Error(`Not main or a release tag: ${JSON.stringify(ref)}`);
  if (!html.includes("{{version}}")) throw new Error("The page has no {{version}} to fill");
  const out = html.replace(/\{\{version\}\}/g, version).replace(/\{\{ref\}\}/g, ref);
  const left = /\{\{\s*[\w.-]+\s*\}\}/.exec(out);
  if (left) throw new Error(`The page keeps ${left[0]}`);
  return out;
}

if (require.main === module) {
  const [file, version, ref] = process.argv.slice(2);
  if (!file || !version || !ref) {
    process.stderr.write("usage: node tools/release/stamp-site.js <page.html> <version> <ref>\n");
    process.exit(2);
  }
  try {
    fs.writeFileSync(file, stamp(fs.readFileSync(file, "utf8"), version, ref));
  } catch (e) {
    process.stderr.write(`stamp-site: ${e.message}\n`);
    process.exit(1);
  }
}

module.exports = { stamp };
