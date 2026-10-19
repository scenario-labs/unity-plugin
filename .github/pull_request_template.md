<!--
The PR title becomes the squash commit header: type(scope): summary.
Use an imperative summary, no trailing period, and at most 120 characters.
Scopes: core, mcp, auth, quote, jobs, schema, types, service, import, ui, agent,
api, tests, tools, docs, ci, deps, release, agents, repo (see commitlint.config.mjs).
Example: fix(import): keep the selection after placing a material

Keep one concern per PR and the description short. Tick only checks you did;
leave the rest unticked without comment. Never claim review, results or
authorization that has not happened.
-->

## What does this PR change?

<!--
One to three sentences: the problem and what changes. Name any action needed
after merge and any known gap. Closes #n for complete issue scope; Refs #n
otherwise, saying what remains. Show a visible change with screenshots of the
window, before and after.
-->

## Validation

- [ ] The EditMode tests pass with graphics on (window tests included); the command and result are listed below.
- [ ] Window changes were tried in a full Unity editor session, in the light and dark editor themes; Unity and OS versions are listed, with before and after screenshots.
- [ ] Agent bridge changes were tried with an MCP client (named below).
- [ ] Live status is stated: `ScenarioDev.ContractCheck.Run` (free) when calls to Scenario change; any paid run had prior authorization for its project and budget.

Commands and results:

<!-- One line per command run and its result. Keep the line above: it stops
GitHub merging this list into the checklist. -->

## Public content

- [ ] No credentials, tokens, signed URL query strings, or private account, project, job or asset ids are included.
- [ ] No personal paths, agent session ids or spend anecdotes are included.

## Rules

- [ ] The spending rules in `AGENTS.md` still hold (price check first, single-use quotes, paid calls sent once, `SCENARIO_NO_SPEND`).
- [ ] No unrelated manual version or changelog edits; documentation matches the resulting behavior.

## Authorship and licensing

- [ ] Human review of the complete diff is recorded before merge.
- [ ] Adopted third-party code or assets keep their compatible licenses and attribution; original contributions are licensed under MIT, the repository license.

<!-- If an agent contributed, name the harness and keep accurate co-author
trailers on commits and in the final squash message. -->
