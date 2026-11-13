# Releasing

Releases are made by `.github/workflows/release-please.yml`, which follows the release workflows of blender-plugin and photoshop-plugin. Nobody edits version fields, edits `CHANGELOG.md`, creates a tag or publishes a release by hand.

## How a release happens

1. Pull requests are squash-merged with a Conventional Commit title (`commitlint.config.mjs`). That title is what the changelog says.
2. On each push to `main`, release-please keeps one pull request open, **chore: release unity-plugin X.Y.Z**. It sets the next version in `Packages/com.scenario.unity/package.json`, `McpClient.Version` and the README (`x-release-please` markers), and writes the entry in `Packages/com.scenario.unity/CHANGELOG.md`. Before 1.0, a `feat` raises the minor version and anything else the patch; a `Release-As: X.Y.Z` footer in a commit sets the version.
3. Merging that pull request is the decision to release. The same workflow run then:
   - creates a draft release targeting the release commit;
   - builds the UPM tarball `com.scenario.unity-X.Y.Z.tgz` from that commit (`tools/release/build-package.js`) and checks it (`tools/release/check-package.js`: the version of the tag, every `.meta`, nothing dev-only, every file the committed one);
   - writes `SHA256SUMS`, attests both files, and attaches them with the Sigstore bundle `com.scenario.unity-X.Y.Z.sigstore.json`;
   - adds a Contributors section to the notes, from the authors of the pull requests they list (`tools/release/contributors.js`; never blocks the release);
   - publishes the draft, which creates the tag `unity-plugin-vX.Y.Z`;
   - opens the next release pull request.

   **publish-changelog** (`.github/workflows/publish-changelog.yml`) then runs on the published release: see [Public changelog](#public-changelog). The website workflow (`.github/workflows/pages.yml`) runs after the release workflow and serves the newest tarball at `unity.scenario.com/download/latest`.

Users install a release with the git URL pinned to its tag (`#unity-plugin-vX.Y.Z`) or from its tarball (README, Install).

The tags `unity-plugin-v0.1.0` and `unity-plugin-v0.2.0` sit on the history that #200 squashed, outside `main`. The `v0.x` tags without a component belong to the earlier plugin (`com.scenarioinc.scenario`); the release workflows ignore them.

## What it needs

Set once by an administrator:

- The release GitHub App installed on this repository, and the organization secrets `GH_ACTIONS_TOKEN_RETRIEVER_PUBLIC_REPO_CLIENT_ID` and `GH_ACTIONS_TOKEN_RETRIEVER_PUBLIC_REPO_PRIVATE_KEY` available to it. Without them every run stops at "Retrieve GitHub App token". Pull requests and releases are made with the App's token because GitHub starts no workflow from an event made with `GITHUB_TOKEN`: the release pull request would get no CI, and publishing the draft would not start publish-changelog.
- The `versioning` tag ruleset on `unity-plugin-v*`, which only the App can bypass.
- Immutable releases, once the first release with a tarball is published and checked: nothing is attached after the publish job.
- The secret `CHANGELOG_INGEST_SECRET` available to this repository (an organization secret, as for the other plugins), and `unity-plugin` known to the changelog ingest of www.scenario.com as a source in the Plugins group (it is, with the title `Plugins - Unity`). Without the secret publish-changelog fails; without the source the ingest answers HTTP 400.
- For `.github/workflows/project.yml`, the App's "Organization projects: Read and write" permission, as for photoshop-plugin.

## Check a release

```sh
gh release download unity-plugin-vX.Y.Z --repo scenario-labs/unity-plugin
sha256sum -c SHA256SUMS
gh attestation verify com.scenario.unity-X.Y.Z.tgz --repo scenario-labs/unity-plugin
# In a clone with the tag fetched: the tarball is that commit's package
node tools/release/check-package.js com.scenario.unity-X.Y.Z.tgz X.Y.Z --ref unity-plugin-vX.Y.Z
```

## Public changelog

`.github/workflows/publish-changelog.yml`, as in blender-plugin and photoshop-plugin, posts the notes of each published stable `unity-plugin-v*` release to the changelog of www.scenario.com, where they show under Plugins with a `Plugins - Unity` title. It runs on `release: published` (the publish job flips the draft with the App's token, so the event starts it) or by hand with a published tag (Actions > publish-changelog > Run workflow). Notes with no `### ` section post nothing.

It checks the answer for this release's version, since an HTTP 200 alone can carry a processing error. `queued` means accepted for processing, which is asynchronous; `already exists` means the version was received before, not that it shows. Either way, check that the entry appears at [scenario.com/changelog](https://www.scenario.com/changelog). After a timeout or an ingest error, check the changelog and the run before re-running: the workflow never posts twice by itself nor asks for an overwrite. If an existing entry does not show, ask the changelog's maintainer. This recovery is apart from the release: it never rebuilds or republishes release files.

## When a release run fails

Re-run the failed jobs of that same workflow run (Actions > release-please). A draft has no tag yet, which hides it from release-please, so `tools/release/find-draft.js` hands the draft back to the run of its own commit only; a run for a later push to `main` stops with "Rerun the original release workflow for <sha>" instead of publishing the draft from newer code. Never publish a draft by hand: it would skip the checks and the files the publish job requires. Recovery is *tested automated* (fake GitHub answers, `tests/tools/release.test.js`) and *not tested* on GitHub.
