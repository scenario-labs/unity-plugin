"use strict";
// What the release scripts share: the gh CLI calls and the shapes they check.
// The workflow passes GH_TOKEN and GH_REPO in the environment, never as
// arguments. Adapted from scenario-labs/photoshop-plugin (MIT).

const { execFileSync } = require("node:child_process");

const PREFIX = "unity-plugin-v";
// The GitHub App that creates release-please's drafts and publishes them.
const RELEASE_APP = "gh-actions-token-retriever-public[bot]";
const REPO_RE = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/;
const SHA_RE = /^[0-9a-fA-F]{40}$/;
const TAG_RE = /^unity-plugin-v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

// A check that failed. Its message is ours and safe to print; any other error
// prints only its name, since API answers and gh output may hold private data.
class ReleaseError extends Error {}

function gh(args) {
  return execFileSync("gh", args, { encoding: "utf8", timeout: 60000, stdio: ["ignore", "pipe", "pipe"] });
}

function ghJson(args) {
  return JSON.parse(gh(args));
}

// [major, minor, patch] of a release tag, or null.
function tagVersion(tag) {
  const m = TAG_RE.exec(tag);
  return m ? m.slice(1).map(Number) : null;
}

function compareVersions(a, b) {
  for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return 0;
}

function failure(prefix, error) {
  const message = error instanceof ReleaseError ? error.message : error && error.name ? error.name : "Error";
  return `::error::${prefix}: ${message}\n`;
}

module.exports = { PREFIX, RELEASE_APP, REPO_RE, SHA_RE, TAG_RE, ReleaseError, gh, ghJson, tagVersion, compareVersions, failure };
