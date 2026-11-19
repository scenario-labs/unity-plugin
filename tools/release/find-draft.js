#!/usr/bin/env node
"use strict";
// Finds a release draft left by an earlier run of the release workflow
// (.github/workflows/release-please.yml), so that rerunning that run finishes
// and publishes it. A draft has no tag yet, which hides it from
// release-please, so without this a failed run would get a second draft. Only
// the run of the draft's own commit may finish it: a later push to main fails
// here instead of publishing the draft from newer code. Adapted from
// scenario-labs/photoshop-plugin (MIT).
//
//   GH_REPO=owner/repo GITHUB_SHA=<sha> GITHUB_OUTPUT=<file> node tools/release/find-draft.js
//
// Writes found=false, or found=true with tag_name, sha and release_id, to
// GITHUB_OUTPUT, and only after every check passed.

const fs = require("node:fs");
const { PREFIX, RELEASE_APP, REPO_RE, SHA_RE, TAG_RE, ReleaseError, ghJson, failure } = require("./github.js");

function findDraft(repo, currentSha, request = ghJson) {
  if (!REPO_RE.test(repo || "")) throw new ReleaseError("GH_REPO must identify an owner/repository");
  if (!SHA_RE.test(currentSha || "")) throw new ReleaseError("GITHUB_SHA must be a full commit SHA");
  const pages = request(["api", `repos/${repo}/releases?per_page=100`, "--paginate", "--slurp"]);
  if (!Array.isArray(pages) || pages.some((page) => !Array.isArray(page))) throw new ReleaseError("GitHub returned an invalid paginated release list");
  const drafts = [];
  for (const page of pages) {
    for (const release of page) {
      if (!release || typeof release !== "object" || Array.isArray(release)) throw new ReleaseError("GitHub returned an invalid release");
      const tag = release.tag_name;
      if (release.draft === true && typeof tag === "string" && tag.startsWith(PREFIX)) drafts.push(release);
    }
  }
  if (drafts.length > 1) throw new ReleaseError("Multiple Unity release drafts exist; resolve them before retrying");
  if (!drafts.length) return { found: "false" };
  const draft = drafts[0];
  if (!TAG_RE.test(draft.tag_name) || draft.prerelease !== false) throw new ReleaseError("The pending draft must have a stable unity-plugin-vX.Y.Z tag");
  if ((draft.author || {}).login !== RELEASE_APP) throw new ReleaseError("The pending draft was not created by the release GitHub App");
  const sha = draft.target_commitish;
  if (typeof sha !== "string" || !SHA_RE.test(sha)) throw new ReleaseError("The pending draft must target a full commit SHA, not a branch");
  if (!Number.isInteger(draft.id) || draft.id <= 0) throw new ReleaseError("The pending draft has no valid release ID");
  if (sha.toLowerCase() !== currentSha.toLowerCase()) {
    throw new ReleaseError(`Rerun the original release workflow for ${sha.toLowerCase()}; newer pushes must not publish this draft`);
  }
  return { found: "true", tag_name: draft.tag_name, sha: sha.toLowerCase(), release_id: String(draft.id) };
}

function main(env = process.env, request = ghJson, err = process.stderr) {
  try {
    const outputs = findDraft(env.GH_REPO, env.GITHUB_SHA, request);
    if (!env.GITHUB_OUTPUT) throw new ReleaseError("GITHUB_OUTPUT is not set");
    fs.appendFileSync(env.GITHUB_OUTPUT, Object.entries(outputs).map(([k, v]) => `${k}=${v}\n`).join(""));
    return 0;
  } catch (error) {
    err.write(failure("Cannot recover a release draft", error));
    return 1;
  }
}

if (require.main === module) process.exit(main());

module.exports = { findDraft, main };
