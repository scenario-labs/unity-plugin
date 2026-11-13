"use strict";
// unity.scenario.com: the page in site/ (published by
// .github/workflows/pages.yml with docs/images/ as images/), its version stamp
// (tools/release/stamp-site.js), and the Vercel project in tools/site_proxy/
// that holds the domain: routing, download counts in PostHog and the
// /download/latest redirect. A local server stands in for GitHub Pages
// (download/latest.json) and for PostHog. The middleware is TypeScript, run
// through Node's type stripping. Adapted from scenario-labs/photoshop-plugin
// (MIT).
//
//   node --test "tests/tools/*.test.js"

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");
const { pathToFileURL } = require("node:url");
const { stamp } = require("../../tools/release/stamp-site.js");

const ROOT = path.resolve(__dirname, "..", "..");
const SITE = path.join(ROOT, "site");
const PROXY = path.join(ROOT, "tools", "site_proxy");
const HTML = fs.readFileSync(path.join(SITE, "index.html"), "utf8");
const PAGES = fs.readFileSync(path.join(ROOT, ".github", "workflows", "pages.yml"), "utf8");
// Served by the Vercel project, not by Pages: the Web Analytics script and the
// redirect to the latest tarball (tools/site_proxy/middleware.ts).
const PROXY_PATHS = ["/page.js", "/download/latest"];
// pages.yml copies docs/images/ into the site as images/.
const COPIED = { "images/": "docs/images/" };

// ------------------------------------------------------------------ the page

test("the site publishes to unity.scenario.com through the Vercel proxy", () => {
  // Pages keeps no custom domain: one would make it redirect the proxy's requests back to it.
  assert.ok(!fs.existsSync(path.join(SITE, "CNAME")), "no CNAME in site/");
  const proxy = JSON.parse(fs.readFileSync(path.join(PROXY, "vercel.json"), "utf8"));
  assert.deepEqual(proxy.rewrites, [
    { source: "/page.js", destination: "https://unity.scenario.com/_vercel/insights/script.js" },
    { source: "/(.*)", destination: "https://scenario-labs.github.io/unity-plugin/$1" },
  ]);
  // Vercel serves any other file in that folder instead of forwarding its path.
  assert.deepEqual(fs.readdirSync(PROXY).filter((n) => n !== ".DS_Store").sort(), ["middleware.ts", "vercel.json"]);
  assert.match(HTML, /<title>Scenario for Unity<\/title>/);
  assert.match(HTML, /<script defer src="\/page\.js"><\/script>/);
  assert.ok(HTML.includes('href="/download/latest"'), "the page links the latest tarball");
  // A folder link without its final slash would make Pages redirect to its own address.
  assert.deepEqual([...HTML.matchAll(/\b(?:src|href)="([^":#?]+\/)"/g)].map((m) => m[1]), []);
});

test("every local file the page links to is published", () => {
  const refs = [...HTML.matchAll(/\b(?:src|href|srcset)="([^"]+)"/g)].map((m) => m[1]);
  const local = refs.filter((u) => !/^(?:[a-z]+:|#|\/\/)/i.test(u) && !PROXY_PATHS.includes(u));
  assert.ok(local.length >= 8, "the page shows the window, the results and the steps");
  for (const u of local) {
    const p = decodeURI(u.split(/[?#]/)[0]);
    assert.ok(!path.isAbsolute(p) && !p.split("/").includes(".."), `${u} stays inside the site`);
    const prefix = Object.keys(COPIED).find((k) => p.startsWith(k));
    const source = prefix ? COPIED[prefix] + p.slice(prefix.length) : `site/${p}`;
    assert.ok(fs.existsSync(path.join(ROOT, source)), `${u} resolves (${source})`);
  }
  for (const [to, from] of Object.entries(COPIED)) assert.ok(PAGES.includes(`cp -R ${from.slice(0, -1)} "$RUNNER_TEMP/site/${to.slice(0, -1)}"`), `pages.yml copies ${from} to ${to}`);
});

test("the page names no version or ref of its own: the publish fills them", () => {
  const { version } = JSON.parse(fs.readFileSync(path.join(ROOT, "Packages", "com.scenario.unity", "package.json"), "utf8"));
  assert.ok(!HTML.includes(version), `the page does not spell out ${version}`);
  assert.ok(!/unity-plugin-v\d|com\.scenario\.unity-\d/.test(HTML), "no release tag or tarball name with a version");
  assert.ok(HTML.includes("{{version}}") && HTML.includes("{{ref}}"));
  assert.ok(!HTML.includes("/blob/main/"), "links to the repository go through {{ref}}");
});

test("stamp fills the version and the ref, and refuses anything else", () => {
  const out = stamp(HTML, "0.3.0", "unity-plugin-v0.3.0");
  assert.ok(!out.includes("{{"));
  assert.ok(out.includes("#unity-plugin-v0.3.0") && out.includes("com.scenario.unity-0.3.0.tgz"));
  assert.ok(out.includes("/blob/unity-plugin-v0.3.0/README.md"));
  assert.ok(stamp(HTML, "0.2.0", "main").includes("/blob/main/Packages/com.scenario.unity/Documentation~/USER_GUIDE.md"));
  assert.throws(() => stamp(HTML, "0.3", "main"), /Not a release version/);
  assert.throws(() => stamp(HTML, "0.3.0", "photoshop-plugin-v0.3.0"), /Not main or a release tag/);
  assert.throws(() => stamp("<p>no version</p>", "0.3.0", "main"), /no \{\{version\}\}/);
  assert.throws(() => stamp("{{version}} {{date}}", "0.3.0", "main"), /keeps \{\{date\}\}/);
});

test("pages.yml publishes the newest release's page and its verified tarball", () => {
  assert.match(PAGES, /^ {2}workflow_run:\n {4}workflows: \[release-please\]\n {4}types: \[completed\]\n {4}branches: \[main\]$/m);
  assert.match(PAGES, /node tools\/release\/stamp-site\.js "\$RUNNER_TEMP\/site\/index\.html" "\$VERSION" "\$REF"/);
  assert.match(PAGES, /sha256sum -c "\$RUNNER_TEMP\/site\/download\/SHA256SUMS"/);
  assert.match(PAGES, /gh attestation verify "\$dir\/\$file" --repo "\$GITHUB_REPOSITORY" \\\n\s+--signer-workflow "\$GITHUB_REPOSITORY\/\.github\/workflows\/release-please\.yml"/);
  assert.match(PAGES, /printf '\{ "version": "%s" \}\\n' "\$version" > "\$RUNNER_TEMP\/site\/download\/latest\.json"/);
});

// ------------------------------------------------------------ the middleware

const FILE = path.join(PROXY, "middleware.ts");
const skip = process.features.typescript ? false : "needs Node's TypeScript type stripping (Node 22.18 or later)";
const BROWSER = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Firefox/140.0";

let proxy;
let server;
let base;
let received = [];
let posthogStatus = 200;
// download/latest.json per stand-in origin: { status, version }.
const latest = new Map();
const latestReads = new Map();
let origins = 0;

test.before(async () => {
  if (skip) return;
  proxy = await import(pathToFileURL(FILE).href);
  server = http.createServer((request, response) => {
    const url = request.url || "";
    const prefix = url.replace(/\/download\/latest\.json$/, "");
    if (request.method === "GET" && latest.has(prefix)) {
      latestReads.set(prefix, (latestReads.get(prefix) || 0) + 1);
      const file = latest.get(prefix);
      response.writeHead(file.status, { "content-type": "application/json" }).end(JSON.stringify({ version: file.version }));
      return;
    }
    let text = "";
    request.on("data", (chunk) => (text += chunk));
    request.on("end", () => {
      received.push({ path: url, body: JSON.parse(text) });
      response.writeHead(posthogStatus).end();
    });
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  base = `http://127.0.0.1:${server.address().port}`;
  process.env.POSTHOG_HOST = base;
  process.env.POSTHOG_PROJECT_TOKEN = "phc_test";
});

test.after(() => {
  if (!server) return;
  server.closeAllConnections();
  server.close();
});

test.beforeEach(() => {
  received = [];
  posthogStatus = 200;
});

// A new stand-in origin, so the middleware's cached version starts empty.
function publish(version, status = 200) {
  const prefix = `/pages-${++origins}`;
  latest.set(prefix, { status, version });
  process.env.SITE_ORIGIN = `${base}${prefix}`;
  return prefix;
}

// One request through the middleware; resolves once its count is sent.
async function visit(pathname, headers = {}, method = "GET") {
  const pending = [];
  const result = proxy.default(new Request(`https://unity.scenario.com${pathname}`, { method, headers }), { waitUntil: (p) => pending.push(p) });
  const response = result ? await result : undefined;
  await Promise.all(pending);
  return { response, counted: pending.length };
}

const TGZ = (v) => `/download/com.scenario.unity-${v}.tgz`;

test("runs only for the download folder, and reads the Pages address vercel.json forwards to", { skip }, () => {
  assert.deepEqual(proxy.config, { matcher: "/download/:path*", runtime: "nodejs" });
  const routing = JSON.parse(fs.readFileSync(path.join(PROXY, "vercel.json"), "utf8"));
  assert.equal(routing.rewrites.at(-1).destination, `${proxy.ORIGIN}/$1`);
});

test("records a download with the plugin, version, client and country only", { skip }, async () => {
  publish("0.3.0");
  const { response } = await visit(TGZ("0.3.0"), { "user-agent": BROWSER, "x-vercel-ip-country": "FR" });
  assert.equal(response, undefined, "the request goes on to Pages unchanged");
  assert.equal(received.length, 1);
  assert.equal(received[0].path, "/i/v0/e/");
  const { api_key, event, distinct_id, properties } = received[0].body;
  assert.equal(api_key, "phc_test");
  assert.equal(event, "extension_downloaded");
  assert.match(distinct_id, /^[0-9a-f-]{36}$/);
  assert.deepEqual(properties, { plugin: "unity", client: "browser", country: "FR", $process_person_profile: false, $geoip_disable: true, extension_version: "0.3.0" });
});

test("uses a new anonymous identifier for every request", { skip }, async () => {
  publish("0.3.0");
  await visit(TGZ("0.3.0"), { "user-agent": BROWSER });
  await visit(TGZ("0.3.0"), { "user-agent": BROWSER });
  assert.notEqual(received[0].body.distinct_id, received[1].body.distinct_id);
});

test("records a version that is not the published one, or an unreadable latest.json, as unknown", { skip }, async () => {
  publish("0.3.0");
  await visit(TGZ("9.9.9"));
  publish("0.3.0", 500);
  await visit(TGZ("0.3.0"));
  assert.deepEqual(received.map((r) => r.body.properties.extension_version), ["unknown", "unknown"]);
});

test("reads latest.json once for several downloads, and again for an unseen version after a minute", { skip }, async (t) => {
  const prefix = publish("0.3.0");
  await visit(TGZ("0.3.0"));
  await visit(TGZ("0.3.0"));
  assert.equal(latestReads.get(prefix), 1);
  latest.set(prefix, { status: 200, version: "0.4.0" });
  await visit(TGZ("0.4.0"));
  assert.equal(latestReads.get(prefix), 1, "not within the minute");
  const now = Date.now();
  t.mock.method(Date, "now", () => now + 61_000);
  await visit(TGZ("0.4.0"));
  assert.equal(latestReads.get(prefix), 2);
  assert.deepEqual(received.map((r) => r.body.properties.extension_version), ["0.3.0", "0.3.0", "unknown", "0.4.0"]);
});

test("sorts clients into crawlers, scripts, browsers and the rest, and keeps only two-letter countries", { skip }, async () => {
  publish("0.3.0");
  const agents = [
    ["Mozilla/5.0 (compatible; Googlebot/2.1)", "bot"],
    ["lychee/0.18", "bot"],
    ["curl/8.7.1", "tool"],
    ["", "tool"],
    [BROWSER, "browser"],
    ["UnityPlayer/6000.3.21f1 (UnityWebRequest/1.0)", "other"],
  ];
  for (const [agent] of agents) await visit(TGZ("0.3.0"), { "user-agent": agent, "x-vercel-ip-country": "not a code" });
  assert.deepEqual(received.map((r) => r.body.properties.client), agents.map(([, client]) => client));
  assert.ok(received.every((r) => !("country" in r.body.properties)));
});

test("ignores other files, HEAD requests and resumed downloads", { skip }, async () => {
  publish("0.3.0");
  for (const p of ["/download/", "/download/latest.json", "/download/SHA256SUMS", "/download/com.scenario.unity-0.3.0.zip", "/download/scenario-photoshop-0.3.0.ccx", "/"]) {
    assert.equal((await visit(p)).counted, 0, p);
  }
  assert.equal((await visit(TGZ("0.3.0"), {}, "HEAD")).counted, 0);
  assert.equal((await visit(TGZ("0.3.0"), { range: "bytes=1000-" })).counted, 0);
  assert.equal((await visit(TGZ("0.3.0"), { range: "bytes=0-" })).counted, 1);
  assert.equal(received.length, 1);
});

test("counts nothing without a PostHog token", { skip }, async () => {
  publish("0.3.0");
  delete process.env.POSTHOG_PROJECT_TOKEN;
  try {
    assert.deepEqual(await visit(TGZ("0.3.0")), { response: undefined, counted: 0 });
  } finally {
    process.env.POSTHOG_PROJECT_TOKEN = "phc_test";
  }
  assert.equal(received.length, 0);
});

test("a failed send is logged and changes nothing", { skip }, async (t) => {
  publish("0.3.0");
  const warn = t.mock.method(console, "warn", () => {});
  posthogStatus = 500;
  const { response } = await visit(TGZ("0.3.0"));
  assert.equal(response, undefined);
  assert.deepEqual(warn.mock.calls.map((c) => c.arguments[0]), ["Download count not recorded: HTTP 500"]);
});

test("/download/latest redirects to the latest tarball without counting it", { skip }, async () => {
  publish("0.3.0");
  for (const method of ["GET", "HEAD"]) {
    const { response, counted } = await visit("/download/latest", {}, method);
    assert.equal(response.status, 302);
    assert.equal(response.headers.get("location"), TGZ("0.3.0"));
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(counted, 0);
  }
  // Before the first release with a tarball, or while latest.json cannot be read: the releases page.
  publish("", 404);
  const { response } = await visit("/download/latest");
  assert.equal(response.headers.get("location"), "https://github.com/scenario-labs/unity-plugin/releases");
  assert.equal(received.length, 0);
});

test("/download/latest reads latest.json on every request, and sends to the releases page when a read fails", { skip }, async () => {
  const prefix = publish("0.3.0");
  await visit(TGZ("0.3.0"));
  latest.set(prefix, { status: 200, version: "0.4.0" });
  assert.equal((await visit("/download/latest")).response.headers.get("location"), TGZ("0.4.0"));
  latest.set(prefix, { status: 500, version: "" });
  assert.equal((await visit("/download/latest")).response.headers.get("location"), "https://github.com/scenario-labs/unity-plugin/releases");
  assert.equal(latestReads.get(prefix), 3);
  await visit(TGZ("0.4.0"));
  assert.equal(received.at(-1).body.properties.extension_version, "0.4.0");
});
