/**
 * Syncs the aspire-config JSON Schema from microsoft/aspire into versioned
 * copies under src/data/schemas/, then updates the version index.
 *
 * Every stable (non-prerelease, non-draft) microsoft/aspire release that ships
 * the schema gets its own copy, so pinned
 * https://aspire.dev/reference/cli/configuration/schema/<version>.json URLs
 * resolve for any shipped release. Existing copies are never re-fetched, and the
 * index's `latest` is always the highest version. The scheduled Integration Data
 * Updater workflow runs this script through `pnpm update:all`, so new releases
 * are picked up automatically.
 *
 * Usage:
 *   pnpm update:schemas                                      # sync every stable release
 *   pnpm update:schemas -- --version 13.2.3                  # sync a single version tag
 *   pnpm update:schemas -- --version 13.3.0 --ref <sha|ref>  # label as 13.3.0 but fetch from
 *                                                            # the given commit SHA or branch ref
 *                                                            # (used before the v<version> tag is
 *                                                            # published)
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

import { fetchWithProxy as fetch } from './fetch-with-proxy';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const FRONTEND_ROOT = path.resolve(__dirname, '..');

const ASPIRE_REPO = 'microsoft/aspire';
const SCHEMA_SOURCE_PATH = 'extension/schemas/aspire-config.schema.json';
const SCHEMAS_DIR = path.join(FRONTEND_ROOT, 'src', 'data', 'schemas');
const INDEX_FILE = path.join(SCHEMAS_DIR, 'index.json');

const SITE_ORIGIN = 'https://aspire.dev';
const SCHEMA_BASE_PATH = '/reference/cli/configuration/schema';

/** The first microsoft/aspire release that ships the schema. */
const MIN_SCHEMA_VERSION = '13.2.0';

/** Stable tags are `v<major>.<minor>.<patch>`; 13.4.4 was tagged `v13.4.4-release`. */
const STABLE_RELEASE_TAG = /^v(\d+\.\d+\.\d+)(?:-release)?$/;

const RELEASES_PAGE_SIZE = 100;

interface GitHubRelease {
  tag_name: string;
  prerelease: boolean;
  draft: boolean;
}

interface SchemaIndex {
  latest: string;
  versions: string[];
}

/** A schema version and the microsoft/aspire git ref to fetch it from. */
interface SchemaSource {
  version: string;
  ref: string;
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** Compare versions numerically, so 13.10.0 sorts after 13.9.0. */
function compareVersions(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true });
}

function getArgValue(name: string): string | undefined {
  const argIndex = process.argv.indexOf(name);
  return argIndex >= 0 ? process.argv[argIndex + 1] : undefined;
}

function schemaFileName(version: string): string {
  return `aspire-config.${version}.schema.json`;
}

function schemaFilePath(version: string): string {
  return path.join(SCHEMAS_DIR, schemaFileName(version));
}

/** Fetch every stable microsoft/aspire release that ships the schema, oldest first. */
async function fetchStableSchemaSources(): Promise<SchemaSource[]> {
  const headers: Record<string, string> = {
    'User-Agent': 'aspire-schema-updater',
    Accept: 'application/vnd.github.v3+json',
  };
  if (process.env.GITHUB_TOKEN) {
    headers['Authorization'] = `token ${process.env.GITHUB_TOKEN}`;
  }

  const sources = new Map<string, SchemaSource>();
  for (let page = 1; ; page++) {
    const url =
      `https://api.github.com/repos/${ASPIRE_REPO}/releases` +
      `?per_page=${RELEASES_PAGE_SIZE}&page=${page}`;
    const res = await fetch(url, { headers });
    if (!res.ok) {
      throw new Error(`Failed to fetch releases: ${res.status} ${res.statusText}`);
    }

    const releases = (await res.json()) as GitHubRelease[];
    for (const release of releases) {
      if (release.prerelease || release.draft) {
        continue;
      }

      const version = STABLE_RELEASE_TAG.exec(release.tag_name)?.[1];
      if (!version) {
        console.warn(`⚠️  Skipping stable release with an unrecognized tag: ${release.tag_name}`);
        continue;
      }

      if (compareVersions(version, MIN_SCHEMA_VERSION) >= 0) {
        sources.set(version, { version, ref: release.tag_name });
      }
    }

    if (releases.length < RELEASES_PAGE_SIZE) {
      break;
    }
  }

  if (sources.size === 0) {
    throw new Error(`No stable ${ASPIRE_REPO} release found at or after ${MIN_SCHEMA_VERSION}`);
  }

  return [...sources.values()].sort((a, b) => compareVersions(a.version, b.version));
}

/** Fetch the raw schema JSON from microsoft/aspire at the given git ref (tag, branch, or SHA). */
async function fetchSchemaAtRef(ref: string): Promise<Record<string, unknown>> {
  const rawUrl =
    `https://raw.githubusercontent.com/${ASPIRE_REPO}/${ref}/${SCHEMA_SOURCE_PATH}`;
  const res = await fetch(rawUrl, {
    headers: { 'User-Agent': 'aspire-schema-updater' },
  });
  if (!res.ok) {
    throw new Error(`Failed to fetch schema at ref ${ref}: ${res.status} ${res.statusText}`);
  }
  return (await res.json()) as Record<string, unknown>;
}

/** Read the current index, or return an empty one if it doesn't exist yet. */
function readIndex(): SchemaIndex {
  if (!fs.existsSync(INDEX_FILE)) {
    return { latest: '', versions: [] };
  }
  return JSON.parse(fs.readFileSync(INDEX_FILE, 'utf-8')) as SchemaIndex;
}

/** Write the index atomically by writing a temp file and renaming into place. */
function writeIndex(index: SchemaIndex): void {
  fs.mkdirSync(SCHEMAS_DIR, { recursive: true });

  const contents = JSON.stringify(index, null, 2) + '\n';
  const tempFile = path.join(
    SCHEMAS_DIR,
    `${path.basename(INDEX_FILE)}.${process.pid}.${Date.now()}.tmp`,
  );

  try {
    fs.writeFileSync(tempFile, contents, 'utf-8');
    fs.renameSync(tempFile, INDEX_FILE);
  } catch (error) {
    if (fs.existsSync(tempFile)) {
      fs.unlinkSync(tempFile);
    }
    throw error;
  }
}

/** Fetch and save the schema for a version unless a copy already exists. Returns true if saved. */
async function syncSchema({ version, ref }: SchemaSource): Promise<boolean> {
  const outFile = schemaFilePath(version);
  if (fs.existsSync(outFile)) {
    return false;
  }

  console.log(`⬇️  Fetching schema for ${version} from ${ASPIRE_REPO} @ ${ref}…`);
  const schema = await fetchSchemaAtRef(ref);

  // Update $id to point to the versioned aspire.dev URL
  schema['$id'] = `${SITE_ORIGIN}${SCHEMA_BASE_PATH}/${version}.json`;

  fs.mkdirSync(SCHEMAS_DIR, { recursive: true });
  fs.writeFileSync(outFile, JSON.stringify(schema, null, 2) + '\n', 'utf-8');
  console.log(`✅ Saved schema to ${path.relative(FRONTEND_ROOT, outFile)}`);
  return true;
}

async function main(): Promise<void> {
  const pinnedVersion = getArgValue('--version')?.replace(/^v/, '');
  const ref = getArgValue('--ref');

  let sources: SchemaSource[];
  if (pinnedVersion) {
    console.log(`📌 Using pinned version: ${pinnedVersion}`);
    sources = [{ version: pinnedVersion, ref: ref ?? `v${pinnedVersion}` }];
  } else if (ref) {
    throw new Error('--ref requires --version to label the schema fetched from that ref');
  } else {
    console.log(`🔍 Fetching stable releases from ${ASPIRE_REPO}…`);
    sources = await fetchStableSchemaSources();
    console.log(`✅ Found ${sources.length} stable releases that ship the schema`);
  }

  let savedCount = 0;
  for (const source of sources) {
    if (await syncSchema(source)) {
      savedCount++;
    }
  }
  if (savedCount === 0) {
    console.log('ℹ️  All schemas already exist; nothing to fetch.');
  }

  // Update the index. `latest` is the highest version, so an out-of-band patch
  // for an older minor release never replaces a newer latest version.
  const index = readIndex();
  index.versions = [...new Set([...index.versions, ...sources.map((s) => s.version)])].sort(
    compareVersions
  );
  index.latest = index.versions[index.versions.length - 1];

  writeIndex(index);
  console.log(`✅ Updated ${path.relative(FRONTEND_ROOT, INDEX_FILE)} — latest: ${index.latest}, versions: [${index.versions.join(', ')}]`);
}

main().catch((error: unknown) => {
  console.error('❌ Error:', getErrorMessage(error));
  process.exit(1);
});
