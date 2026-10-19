// Single source of truth for commit headers and PR titles. CI installs
// commitlint from pnpm-lock.yaml (.github/workflows/pr-name.yml and
// commitlint.yml). The same check locally, with the Node version in .nvmrc:
//
//   pnpm install --frozen-lockfile
//   printf '%s\n' "feat(ui): my title" | pnpm exec commitlint --verbose

// U+2014 written as a code point so that this file contains no em dash itself.
const EM_DASH = String.fromCharCode(0x2014);

/** @type {import('@commitlint/types').Plugin} */
const houseRules = {
  rules: {
    // House style: no em dashes anywhere, commit messages and PR titles included.
    "no-em-dash": ({ raw }) => [
      !(raw ?? "").includes(EM_DASH),
      "no em dashes in commit messages or PR titles (house style)",
    ],
  },
};

/** @type {import('@commitlint/types').UserConfig} */
export default {
  extends: ["@commitlint/config-conventional"],
  plugins: [houseRules],
  // Merge commits ("Merge feat/x: ...") come from git merge --no-ff and carry no type.
  ignores: [(message) => message.startsWith("Merge ")],
  rules: {
    "header-max-length": [2, "always", 120],
    // Off: long URLs and Co-Authored-By trailers exceed the 100-character default.
    "body-max-line-length": [0, "always", 200],
    "footer-max-line-length": [0, "always", 200],
    // Off: Unity, MCP, GLB, PBR and URP start many subjects.
    "subject-case": [0],
    // Warn, never fail: the release-please PR is "chore: release X.Y.Z" with no scope.
    "scope-empty": [1, "never"],
    "scope-enum": [
      2,
      "always",
      [
        "core", // Editor/Core cross-cutting: results, JSON, files, transport
        "mcp", // McpClient.cs and Sse.cs: Scenario MCP calls, sessions, replies
        "auth", // Auth.cs, OAuth.cs, CredentialStore.cs: sign-in and secrets
        "quote", // Quote.cs: prices, fingerprints, single-use quotes
        "jobs", // JobLedger.cs: durable job list, states, reconcile
        "schema", // SchemaForm.cs: schemas and workflow inputs to forms and payloads
        "types", // OutputTypes.cs and Catalog.cs: output types, quick picks, steps, catalog
        "service", // Services/ScenarioService.cs: the shared action layer
        "import", // Editor/Unity: importer, placer, builders, provenance, context menus
        "ui", // Editor/UI: the window, views, overlays, styles, icons
        "agent", // Editor/Agent: the MCP bridge, its tools and client setup
        "api", // ScenarioApi.cs: the public C# API
        "tests", // test harness only; a test for a product area keeps the product scope
        "tools", // tools/ and Assets/ScenarioDev
        "docs", // README.md, Documentation~, docs/
        "ci", // .github/workflows
        "deps", // pins: Unity, packages, actions, npm
        "release", // release-please config and manifest, version fields, CHANGELOG plumbing
        "agents", // AGENTS.md, .claude/
        "repo", // LICENSE, community files, templates, editor and lint configs
      ],
    ],
    "no-em-dash": [2, "always"],
  },
};
