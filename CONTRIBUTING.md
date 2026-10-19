# Contributing to Scenario for Unity

Thanks for helping. This guide covers what a pull request needs; the commands,
rules and code map live in [AGENTS.md](AGENTS.md), which humans and coding
agents both follow.

## Before you start

- For anything larger than a small fix, open a change request first
  ([issue chooser](https://github.com/scenario-labs/unity-plugin/issues/new/choose))
  so the approach can be agreed before you write it.
- Read the spending rules in `AGENTS.md`: price check first, single-use quotes,
  paid calls sent once, `SCENARIO_NO_SPEND=1` honored. Every change keeps them.

## Set up

- Unity 6000.3.21f1 (Unity Hub installs it from `ProjectSettings/ProjectVersion.txt`).
  Open the repository root as a project: the package is embedded in `Packages/`.
- Node.js (the version in `.nvmrc`) and pnpm for commit lint: `pnpm install`.
- [uv](https://docs.astral.sh/uv/) to rebuild the icons (`tools/build_icons.py`).

## Run the checks

Run the EditMode tests from **Window > General > Test Runner**, or in batch mode
as shown in AGENTS.md (0 CU, a scripted Scenario). Live checks against Scenario
need a test key; the paid acceptance run spends Creative Units and needs
authorization for the project and budget first.

## Commits and pull requests

- Branch from `main`. Pull requests are squash-merged: the PR title becomes the
  commit header and the changelog line, so it follows Conventional Commits with
  a scope from `commitlint.config.mjs`, for example
  `fix(import): keep the selection after placing a material`.
- Keep one concern per pull request and fill the template briefly: the checks
  you ran, any action needed after merge and any known gap. Tick only what you ran.
- Do not edit versions or `CHANGELOG.md`: release-please does.
- Show UI changes with before and after screenshots of the window, attached to
  the pull request rather than committed.

## Coding agents

Agents follow AGENTS.md. Name the harness in the pull request and keep accurate
co-author trailers on commits and in the squash message. A human reviews the
complete diff before merge.

## Licensing

The repository is under the [MIT License](LICENSE). By contributing, you agree
that your contributions are licensed under the same terms. Code or assets
adopted from elsewhere keep their compatible license and attribution.
