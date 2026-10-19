# Security Policy

## Reporting a vulnerability

Do not report security vulnerabilities through public GitHub issues or pull
requests. Public reports can expose other users before a fix is available, and
copies may remain accessible after deletion.

Report privately through any of these channels:

- [Open a private vulnerability report](https://github.com/scenario-labs/unity-plugin/security/advisories/new)
  from this repository's Security tab.
- Email [support@scenario.com](mailto:support@scenario.com) with "security" in the subject.
- Use in-app support at [app.scenario.com](https://app.scenario.com).

Include the affected surface (the Unity package or the Scenario platform), the
Unity and package versions, reproduction steps and the impact you observe. The
package version is shown in the Package Manager and in Settings > About in the
Scenario window. Use synthetic credentials and minimal sample projects where
possible. Scenario support routes reports to the appropriate team and follows up
through the same private channel.

## Scope

This repository ships Scenario for Unity, an editor package. It stores Scenario
credentials, calls the Scenario MCP server at `mcp.scenario.com`, downloads
generated files and imports them into the open project, and can serve a local
MCP server to AI agents. Relevant reports include:

- Unexpected exposure of tokens, keys or secrets through the project folder,
  logs, errors, provenance data or network requests, including the OS keychain
  entries, the user-only account file, the agent bridge token, and
  `SCENARIO_API_KEY` / `SCENARIO_API_SECRET`.
- The agent bridge accepting a request without the bearer token, from a
  non-local Host or Origin, or from another machine.
- Paid requests sent twice, sent without a matching price check, or sent while
  spending is turned off.
- Unsafe downloads or imports of images, GLB, audio, video or other generated
  content, including files written outside `Assets/`.
- The repository's CI and release supply chain.

Platform vulnerabilities involving `app.scenario.com`, `api.cloud.scenario.com`
or `mcp.scenario.com` use the same private channels above, not this repository's
public issue tracker.

## Supported versions

Security fixes target the [latest release](https://github.com/scenario-labs/unity-plugin/releases).
Older releases have no support branch.

## Secrets in issues and pull requests

API keys and secrets, OAuth tokens, the agent bridge token and signed asset URLs
are credentials. Never commit them or paste them into public issues or pull
requests. Check screenshots, logs and sample projects before sharing them. If a
secret is already exposed, report it privately and revoke the key in Scenario
(or regenerate the bridge token in the Agent view).
