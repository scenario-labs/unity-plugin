// Counts package downloads on unity.scenario.com (adapted from
// scenario-labs/photoshop-plugin, MIT). It runs before vercel.json forwards
// the request to GitHub Pages and never changes the response: the event is
// sent after the request continues, and any failure only loses that count.
// Nothing identifies the visitor: no IP address, no stable identifier, only
// the fields built below. A tarball keeps its version only when
// download/latest.json names it; any other name, which Pages cannot serve,
// counts as "unknown".
//
// /download/latest answers itself with a redirect to the latest tarball, so a
// page can link one address that never changes. It reads latest.json on every
// request: each publish keeps only the newest tarball, so a remembered version
// could point at a file that is gone. The redirect is not counted: the
// download it leads to is.

export const config = { matcher: "/download/:path*", runtime: "nodejs" };
// The GitHub Pages copy of the site: vercel.json forwards every request there.
export const ORIGIN = "https://scenario-labs.github.io/unity-plugin";

interface RequestContext {
  waitUntil(promise: Promise<unknown>): void;
}

type Properties = Record<string, string | boolean>;

// Crawlers, link checkers and previews, matched before browsers because many
// announce themselves as "Mozilla/5.0 (compatible; ...bot...)".
const BOT = /bot|crawl|spider|slurp|facebookexternalhit|preview|lychee|linkcheck|headless|lighthouse/i;
// Scripts and command-line tools; an empty User-Agent counts as one too.
const TOOL = /^(curl|wget|python|go-http-client|node|undici|axios|okhttp|java|libwww|httpie|aiohttp)|powershell/i;
const PACKAGE = /^\/download\/com\.scenario\.unity-(\d+\.\d+\.\d+)\.tgz$/;
const VERSION = /^\d+\.\d+\.\d+$/;
const TIMEOUT_MS = 2000;
// The published version is cached; an unseen version rereads at most once a minute.
const LATEST_TTL_MS = 5 * 60_000;
const LATEST_RETRY_MS = 60_000;
const published = new Map<string, { version: string; at: number }>();

// Read through globalThis: Vercel type-checks this file without Node's types.
function env(name: string): string | undefined {
  return (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env?.[name];
}

/** Who made the request: a crawler, a script, a browser or something else. */
function classify(agent: string): string {
  if (BOT.test(agent)) return "bot";
  if (!agent || TOOL.test(agent)) return "tool";
  if (agent.startsWith("Mozilla/")) return "browser";
  return "other";
}

function describe(request: Request): { version: string; properties: Properties } | undefined {
  if (request.method !== "GET") return undefined;
  // A resumed download already counted when it started.
  const range = request.headers.get("range");
  if (range && !range.startsWith("bytes=0-")) return undefined;
  const found = PACKAGE.exec(new URL(request.url).pathname);
  if (!found) return undefined;

  // The PostHog project is shared with other Scenario plugins.
  const properties: Properties = { plugin: "unity", client: classify(request.headers.get("user-agent") ?? "") };
  const country = request.headers.get("x-vercel-ip-country") ?? "";
  if (/^[A-Z]{2}$/.test(country)) properties.country = country;
  // Anonymous events: no person profile, and no location from the proxy's own address.
  properties.$process_person_profile = false;
  properties.$geoip_disable = true;
  return { version: found[1], properties };
}

/**
 * The version download/latest.json names, read at most every `ttl` (five
 * minutes by default), or once a minute for an unseen one. A failed read
 * keeps the last version read.
 */
async function latestVersion(wanted?: string, ttl = LATEST_TTL_MS): Promise<string> {
  // Read latest.json from Pages directly: through this site it would pass this middleware again.
  const origin = env("SITE_ORIGIN") || ORIGIN;
  const now = Date.now();
  const cached = published.get(origin);
  const age = cached ? now - cached.at : Infinity;
  if (cached && age < ttl && (wanted === undefined || cached.version === wanted || age < LATEST_RETRY_MS)) {
    return cached.version;
  }
  // Keep the last version read when the read fails; an unreadable file only makes this count unknown.
  const latest = (await readLatest(origin)) ?? cached?.version ?? "";
  published.set(origin, { version: latest, at: now });
  return latest;
}

/** One read of download/latest.json: its version, "" when it names none, or null when it cannot be read. */
async function readLatest(origin: string): Promise<string | null> {
  try {
    const response = await fetch(`${origin}/download/latest.json`, { signal: AbortSignal.timeout(TIMEOUT_MS) });
    if (!response.ok) return null;
    const version = ((await response.json()) as { version?: unknown }).version;
    return typeof version === "string" && VERSION.test(version) ? version : "";
  } catch {
    return null;
  }
}

async function send(token: string, version: string, properties: Properties): Promise<void> {
  properties.extension_version = (await latestVersion(version)) === version ? version : "unknown";
  const host = env("POSTHOG_HOST") || "https://us.i.posthog.com";
  try {
    const response = await fetch(`${host}/i/v0/e/`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ api_key: token, event: "extension_downloaded", distinct_id: crypto.randomUUID(), properties }),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    if (!response.ok) console.warn(`Download count not recorded: HTTP ${response.status}`);
  } catch (error) {
    console.warn(`Download count not recorded: ${(error as Error).name}`);
  }
}

/**
 * /download/latest: a redirect to the latest tarball on this site, read
 * afresh, or to the releases page before the first release with a tarball or
 * when latest.json cannot be read. A version remembered from an earlier read
 * could name a tarball Pages no longer serves, since it keeps the latest one
 * only.
 */
async function redirectToLatest(): Promise<Response> {
  const origin = env("SITE_ORIGIN") || ORIGIN;
  const version = await readLatest(origin);
  if (version !== null) published.set(origin, { version, at: Date.now() });
  const location = version
    ? `/download/com.scenario.unity-${version}.tgz`
    : "https://github.com/scenario-labs/unity-plugin/releases";
  return new Response(null, { status: 302, headers: { location, "cache-control": "no-store" } });
}

export default function middleware(request: Request, context: RequestContext): Promise<Response> | undefined {
  const path = new URL(request.url).pathname;
  if (path === "/download/latest" && (request.method === "GET" || request.method === "HEAD")) return redirectToLatest();
  const token = env("POSTHOG_PROJECT_TOKEN");
  const described = token ? describe(request) : undefined;
  if (token && described) context.waitUntil(send(token, described.version, described.properties));
  return undefined;
}
