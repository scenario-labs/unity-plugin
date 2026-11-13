"use strict";
// Release automation (.github/workflows/release-please.yml): the draft
// recovery and contributor scripts (tools/release/, with fake gh answers),
// release-please's tag shape, and the workflows that publish the release and
// its notes. Adapted from scenario-labs/photoshop-plugin (MIT).
//
//   node --test "tests/tools/*.test.js"

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { findDraft, main: findDraftMain } = require("../../tools/release/find-draft.js");
const { linkedNumbers, humans, mentionList, withContributors, update, main: contributorsMain } = require("../../tools/release/contributors.js");

const ROOT = path.resolve(__dirname, "..", "..");
const read = (p) => fs.readFileSync(path.join(ROOT, p), "utf8");
const json = (p) => JSON.parse(read(p));
const PKG = json("release-please-config.json").packages["."];
const WORKFLOW = read(".github/workflows/release-please.yml");

// A job's block in a workflow file.
function job(text, name) {
  return text.split(new RegExp(`^ {2}${name}:\\n`, "m"))[1].split(/^ {2}\S/m)[0];
}

test("release-please drafts unity-plugin-vX.Y.Z from the package version", () => {
  assert.equal(PKG.component, "unity-plugin");
  assert.equal(PKG["include-component-in-tag"], true);
  assert.equal(PKG["include-v-in-tag"], true);
  assert.equal(PKG["tag-separator"], "-");
  assert.equal(PKG.draft, true, "the workflow finishes a draft, then publishes it");
  assert.ok(PKG["extra-files"].some((f) => f.path === "Packages/com.scenario.unity/package.json" && f.jsonpath === "$.version"));
  assert.deepEqual(json(".release-please-manifest.json"), { ".": json("Packages/com.scenario.unity/package.json").version });
});

// ------------------------------------------------------------- find-draft.js

const REPO = "scenario-labs/unity-plugin";
const SHA = "a".repeat(40);
const OTHER = "b".repeat(40);
const APP = "gh-actions-token-retriever-public[bot]";
const draft = (over = {}) => ({ id: 7, tag_name: "unity-plugin-v0.3.0", draft: true, prerelease: false, target_commitish: SHA, author: { login: APP }, ...over });
const releasesAnswer = (pages) => (args) => {
  assert.deepEqual(args, ["api", `repos/${REPO}/releases?per_page=100`, "--paginate", "--slurp"]);
  return pages;
};

test("find-draft: no draft, or drafts of other tags, means a fresh release-please run", () => {
  const pages = [[draft({ draft: false }), draft({ tag_name: "blender-plugin-v0.8.0" }), { id: 1, tag_name: "v0.11.0", draft: true }]];
  assert.deepEqual(findDraft(REPO, SHA, releasesAnswer(pages)), { found: "false" });
});

test("find-draft: the original run recovers its one verified draft, from any page", () => {
  const pages = [[draft({ draft: false, tag_name: "unity-plugin-v0.2.0" })], [draft({ target_commitish: SHA.toUpperCase() })]];
  assert.deepEqual(findDraft(REPO, SHA, releasesAnswer(pages)), { found: "true", tag_name: "unity-plugin-v0.3.0", sha: SHA, release_id: "7" });
});

test("find-draft: two drafts, an unsafe draft or another commit fail instead of choosing", () => {
  assert.throws(() => findDraft(REPO, SHA, releasesAnswer([[draft(), draft({ id: 8 })]])), /Multiple Unity release drafts/);
  const unsafe = [
    [{ tag_name: "unity-plugin-v0.3" }, /stable unity-plugin-vX.Y.Z tag/],
    [{ prerelease: true }, /stable unity-plugin-vX.Y.Z tag/],
    [{ author: { login: "someone" } }, /not created by the release GitHub App/],
    [{ author: null }, /not created by the release GitHub App/],
    [{ target_commitish: "main" }, /full commit SHA, not a branch/],
    [{ id: "7" }, /no valid release ID/],
    [{ id: 0 }, /no valid release ID/],
  ];
  for (const [over, message] of unsafe) assert.throws(() => findDraft(REPO, SHA, releasesAnswer([[draft(over)]])), message, JSON.stringify(over));
  assert.throws(() => findDraft(REPO, OTHER, releasesAnswer([[draft()]])), /Rerun the original release workflow for a{40}; newer pushes must not publish/);
  assert.throws(() => findDraft("not a repo", SHA, releasesAnswer([])), /GH_REPO/);
  assert.throws(() => findDraft(REPO, "abc", releasesAnswer([])), /GITHUB_SHA/);
  assert.throws(() => findDraft(REPO, SHA, () => [{}]), /invalid paginated release list/);
});

test("find-draft: outputs are written only after every check, and failures print no API data", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "find-draft-"));
  try {
    const out = path.join(dir, "out");
    const err = { text: "", write(s) { this.text += s; } };
    const env = { GH_REPO: REPO, GITHUB_SHA: SHA, GITHUB_OUTPUT: out };
    assert.equal(findDraftMain(env, releasesAnswer([[draft()]]), err), 0);
    assert.equal(fs.readFileSync(out, "utf8"), `found=true\ntag_name=unity-plugin-v0.3.0\nsha=${SHA}\nrelease_id=7\n`);
    fs.rmSync(out);
    assert.equal(findDraftMain({ ...env, GITHUB_SHA: OTHER }, releasesAnswer([[draft()]]), err), 1);
    assert.equal(fs.existsSync(out), false);
    const secret = Object.assign(new Error("HTTP 401 with token ghs_secret"), { name: "Error" });
    assert.equal(findDraftMain(env, () => { throw secret; }, err), 1);
    assert.match(err.text, /::error::Cannot recover a release draft: Rerun the original/);
    assert.match(err.text, /::error::Cannot recover a release draft: Error\n$/);
    assert.ok(!err.text.includes("ghs_secret"));
    assert.equal(fs.existsSync(out), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ----------------------------------------------------------- contributors.js

const NOTES = [
  "## [0.3.0](https://github.com/scenario-labs/unity-plugin/compare/unity-plugin-v0.2.0...unity-plugin-v0.3.0) (2026-11-20)",
  "",
  "### Features",
  "",
  "* **auth:** sign in on another device ([#202](https://github.com/scenario-labs/unity-plugin/issues/202)) ([abc1234](https://github.com/scenario-labs/unity-plugin/commit/abc1234))",
  "* **ui:** closes [#135](https://github.com/scenario-labs/unity-plugin/issues/135) ([#210](https://github.com/scenario-labs/unity-plugin/issues/210)) ([def5678](https://github.com/scenario-labs/unity-plugin/commit/def5678))",
  "* **deps:** bump ([#203](https://github.com/scenario-labs/unity-plugin/issues/203)) ([0a0a0a0](https://github.com/scenario-labs/unity-plugin/commit/0a0a0a0))",
  "* **docs:** again ([#202](https://github.com/scenario-labs/unity-plugin/issues/202))",
  "* elsewhere ([#9](https://github.com/scenario-labs/photoshop-plugin/issues/9))",
  "",
].join("\n");
const AUTHORS = { 202: "qchantel", 203: "dependabot[bot]", 210: "QChantel" };

test("contributors: the pull requests the notes link, once each, for this repository only", () => {
  assert.deepEqual(linkedNumbers(NOTES, REPO), [202, 135, 210, 203]);
  assert.deepEqual(humans(["qchantel", "dependabot[bot]", "QChantel", null, "hervenivon"]), ["hervenivon", "qchantel"]);
  assert.equal(mentionList(["a"]), "@a");
  assert.equal(mentionList(["a", "b"]), "@a and @b");
  assert.equal(mentionList(["a", "b", "c"]), "@a, @b and @c");
});

test("contributors: the section follows the notes, once, and only for people", () => {
  assert.equal(withContributors("## [0.3.0](link)\n\n### Features\n\n* thing\n\n", ["qchantel", "hervenivon"]), "## [0.3.0](link)\n\n### Features\n\n* thing\n\n### Contributors\n\nThanks to @hervenivon and @qchantel.\n");
  assert.equal(withContributors("notes\n\n### Contributors\n\nThanks.\n", ["qchantel"]), null);
  assert.equal(withContributors("notes", ["dependabot[bot]", null]), null);
});

// A fake gh: the draft, and each pull request's author (an issue answers 404).
function fakeGh({ isDraft = true, target = SHA, body = NOTES } = {}) {
  const calls = [];
  const request = (args) => {
    calls.push(args);
    if (args[0] === "release") return { body, isDraft, targetCommitish: target };
    const pull = new RegExp(`^repos/${REPO}/pulls/(\\d+)$`).exec(args[1] || "");
    if (args[0] === "api" && pull) {
      if (!AUTHORS[pull[1]]) throw new Error("HTTP 404");
      return { user: { login: AUTHORS[pull[1]] } };
    }
    throw new Error(`unexpected ${args.join(" ")}`);
  };
  const edits = [];
  const run = (args) => edits.push({ args, notes: fs.readFileSync(args[4], "utf8") });
  return { calls, request, run, edits };
}

test("contributors: the draft thanks the authors of the pull requests it lists", () => {
  const g = fakeGh();
  assert.equal(update(REPO, "unity-plugin-v0.3.0", SHA, g.request, g.run), true);
  assert.equal(g.edits.length, 1);
  assert.deepEqual(g.edits[0].args.slice(0, 4), ["release", "edit", "unity-plugin-v0.3.0", "--notes-file"]);
  assert.ok(g.edits[0].notes.startsWith(NOTES.trimEnd()));
  assert.match(g.edits[0].notes, /\n\n### Contributors\n\nThanks to @qchantel\.\n$/);
  assert.equal(fs.existsSync(g.edits[0].args[4]), false, "the notes file is removed");
});

test("contributors: a published release, another commit or notes without people change nothing", () => {
  for (const opts of [{ isDraft: false }, { target: OTHER }]) {
    const g = fakeGh(opts);
    assert.throws(() => update(REPO, "unity-plugin-v0.3.0", SHA, g.request, g.run), /is not the draft/);
    assert.equal(g.edits.length, 0);
  }
  const bots = fakeGh({ body: "* bump ([#203](https://github.com/scenario-labs/unity-plugin/issues/203))\n" });
  assert.equal(update(REPO, "unity-plugin-v0.3.0", SHA, bots.request, bots.run), false);
  assert.equal(bots.edits.length, 0);
  assert.throws(() => update(REPO, "v0.3.0", SHA, fakeGh().request, fakeGh().run), /stable release tag/);
  // Only a 404 means "an issue, not a pull request": any other failure stops
  // the update, so no Contributors section is saved with someone missing.
  const flaky = fakeGh();
  const request = (args) => {
    if (args[1] === `repos/${REPO}/pulls/210`) throw Object.assign(new Error("Command failed: gh api"), { stderr: "gh: Bad Gateway (HTTP 502)\n" });
    return flaky.request(args);
  };
  assert.throws(() => update(REPO, "unity-plugin-v0.3.0", SHA, request, flaky.run), /Cannot read pull request #210/);
  assert.equal(flaky.edits.length, 0);
  // gh's own wording for a missing pull request, on stderr.
  const issue = fakeGh();
  const issueRequest = (args) => {
    if (args[1] === `repos/${REPO}/pulls/135`) throw Object.assign(new Error("Command failed: gh api"), { stderr: "gh: Not Found (HTTP 404)\n" });
    return issue.request(args);
  };
  assert.equal(update(REPO, "unity-plugin-v0.3.0", SHA, issueRequest, issue.run), true);
  const out = { text: "", write(s) { this.text += s; } };
  const err = { text: "", write(s) { this.text += s; } };
  const g = fakeGh({ isDraft: false });
  assert.equal(contributorsMain({ GH_REPO: REPO, TAG: "unity-plugin-v0.3.0", RELEASE_SHA: SHA }, g.request, g.run, out, err), 1);
  assert.equal(out.text, "");
  assert.equal(err.text, `::error::Cannot list release contributors: unity-plugin-v0.3.0 is not the draft for ${SHA}\n`);
});

// ------------------------------------------------------------------ workflows

test("the release workflow recovers drafts with find-draft.js and thanks contributors before publishing", () => {
  assert.match(WORKFLOW, /^name: release-please$/m, "pages and other workflows follow it by this name");
  assert.match(job(WORKFLOW, "release-please"), /- name: Find an unfinished draft before creating another release\n\s+id: pending\n[\s\S]*?run: node tools\/release\/find-draft\.js\n/);
  const publish = job(WORKFLOW, "publish");
  const thank = publish.indexOf("- name: Thank the release's contributors in the draft notes");
  assert.ok(thank > 0 && thank < publish.indexOf("- name: Publish the draft"), "the thank-you edits the draft before it is published");
  assert.match(publish, /- name: Thank the release's contributors in the draft notes\n\s+continue-on-error: true\n[\s\S]*?run: node tools\/release\/contributors\.js\n/);
  // Every write goes through the release App's token.
  for (const m of WORKFLOW.matchAll(/^\s+(?:token|GH_TOKEN): (.+)$/gm)) assert.equal(m[1], "${{ steps.app-token.outputs.token }}");
});

test("each published release posts its notes to www.scenario.com/changelog", () => {
  const text = read(".github/workflows/publish-changelog.yml");
  // The publish job flips the draft with the App's token, so release: published starts this workflow.
  assert.match(text, /^on:\n {2}release:\n {4}types: \[published\]\n {2}workflow_dispatch:\n/m);
  assert.match(text, /github\.repository == 'scenario-labs\/unity-plugin' &&\n\s+startsWith\(github\.event\.release\.tag_name \|\| github\.event\.inputs\.tag, 'unity-plugin-v'\)/, "this package's releases only, not the earlier plugin's v0.x");
  assert.match(text, /\.isDraft == false and \.isPrerelease == false/, "stable published releases only");
  assert.match(text, /--arg repo unity-plugin '\{repo: \$repo, changelog: \.\}'/, "the website knows this source as unity-plugin");
  assert.match(text, /--request POST https:\/\/www\.scenario\.com\/api\/changelog\/ingest/);
  assert.match(text, /version="\$\{TAG#unity-plugin-v\}"/, "the version checked in the answer is the tag's");
  // The secret goes to curl on stdin, never on its command line.
  assert.match(text, /printf 'header = "Authorization: Bearer %s"\\n' "\$CHANGELOG_INGEST_SECRET" \\\n\s+\| curl --config -/);
  assert.ok(!/-H "Authorization/.test(text) && !/--header "Authorization/.test(text));
});
