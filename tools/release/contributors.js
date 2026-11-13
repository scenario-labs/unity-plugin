#!/usr/bin/env node
"use strict";
// Thanks a draft release's pull request authors: release-please's notes name
// no authors. Adapted from scenario-labs/photoshop-plugin (MIT). The workflow
// runs it with continue-on-error: a missing thank-you never holds back a
// release.
//
//   GH_REPO=owner/repo TAG=unity-plugin-vX.Y.Z RELEASE_SHA=<sha> node tools/release/contributors.js
//
// Photoshop asks GitHub to generate notes since the previous release tag. Here
// the unity-plugin-v0.1.0 and v0.2.0 tags sit on a history that main does not
// share (#200 squashed it), so GitHub cannot compare them with a release
// commit. The authors come instead from the pull requests the notes link
// (release-please writes "([#N](https://github.com/<repo>/issues/N))" for each
// squashed pull request); a linked issue that is not a pull request is skipped.

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { REPO_RE, SHA_RE, TAG_RE, ReleaseError, gh, ghJson, failure } = require("./github.js");

const HEADING = "### Contributors";

// The pull request numbers the notes link for `repo`, in order, once each.
function linkedNumbers(body, repo) {
  const escaped = repo.replace(/[.]/g, "\\.");
  const re = new RegExp(`\\[#(\\d+)\\]\\(https://github\\.com/${escaped}/(?:issues|pull)/(\\d+)\\)`, "g");
  const seen = new Set();
  for (const m of body.matchAll(re)) if (m[1] === m[2]) seen.add(Number(m[1]));
  return [...seen];
}

// The human logins among `logins`, one spelling each, sorted.
function humans(logins) {
  const unique = new Map();
  for (const login of logins) {
    if (typeof login === "string" && login && !login.endsWith("[bot]") && !unique.has(login.toLowerCase())) unique.set(login.toLowerCase(), login);
  }
  return [...unique.keys()].sort().map((k) => unique.get(k));
}

function mentionList(logins) {
  const names = logins.map((l) => `@${l}`);
  return names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

// The body with a Contributors section, or null when there is nothing to add.
function withContributors(body, logins) {
  if (new RegExp(`^${HEADING}\\s*$`, "m").test(body)) return null;
  const people = humans(logins);
  if (!people.length) return null;
  return `${body.trimEnd()}\n\n${HEADING}\n\nThanks to ${mentionList(people)}.\n`;
}

// The author of pull request `number`, or null when the number is an issue.
function author(repo, number, request) {
  let pull;
  try {
    pull = request(["api", `repos/${repo}/pulls/${number}`]);
  } catch {
    return null;
  }
  return pull && pull.user && typeof pull.user.login === "string" ? pull.user.login : null;
}

function update(repo, tag, sha, request = ghJson, run = gh) {
  if (!REPO_RE.test(repo || "")) throw new ReleaseError("GH_REPO must identify an owner/repository");
  if (!TAG_RE.test(tag || "") || !SHA_RE.test(sha || "")) throw new ReleaseError("TAG and RELEASE_SHA must be a stable release tag and a full commit SHA");
  const draft = request(["release", "view", tag, "--json", "body,isDraft,targetCommitish"]);
  if (draft.isDraft !== true || String(draft.targetCommitish || "").toLowerCase() !== sha.toLowerCase()) {
    throw new ReleaseError(`${tag} is not the draft for ${sha}`);
  }
  const body = draft.body || "";
  const logins = linkedNumbers(body, repo).map((n) => author(repo, n, request));
  const next = withContributors(body, logins);
  if (next === null) return false;
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "release-notes-"));
  try {
    const file = path.join(dir, "notes.md");
    fs.writeFileSync(file, next);
    run(["release", "edit", tag, "--notes-file", file]);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
  return true;
}

function main(env = process.env, request = ghJson, run = gh, out = process.stdout, err = process.stderr) {
  try {
    const changed = update(env.GH_REPO, env.TAG, env.RELEASE_SHA, request, run);
    out.write(changed ? "Added the Contributors section\n" : "No Contributors section to add\n");
    return 0;
  } catch (error) {
    err.write(failure("Cannot list release contributors", error));
    return 1;
  }
}

if (require.main === module) process.exit(main());

module.exports = { linkedNumbers, humans, mentionList, withContributors, update, main };
