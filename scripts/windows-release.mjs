import { execFileSync, spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { appendFileSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// Rilascio Windows di AIUsageMonitor, eseguito dalla CI a ogni push su main.
//   prepare: calcola versione e tag dai conventional commit e dalle release GitHub, senza modificare file;
//            la versione arriva a `dotnet publish` con -p:Version=<versione>.
//   publish: SHA256SUMS.txt dei due eseguibili in release/, bozza, upload verificato, poi Latest.

/** Progetto la cui <Version> e' la base di versione (la CI la sovrascrive con -p:Version). */
export const projectFile = 'src/AIUsageMonitor.App/AIUsageMonitor.App.csproj'
export const checksumManifestName = 'SHA256SUMS.txt'

const stableVersion = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/

function versionParts(version) {
  if (typeof version !== 'string' || !stableVersion.test(version))
    throw new Error(`Versione SemVer stabile non valida: ${version}`)
  return version.split('.').map(Number)
}

export function compareVersions(left, right) {
  const a = versionParts(left)
  const b = versionParts(right)
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2]
}

export function getBump(messages) {
  let bump = 'patch'
  for (const message of messages) {
    // Anche le righe successive: un commit di squash puo' contenere piu' intestazioni.
    if (
      /^[a-z][\w-]*(?:\([^\r\n)]+\))?!: .+/im.test(message) ||
      /^BREAKING[ -]CHANGE: .+/m.test(message)
    )
      return 'major'
    if (/^feat(?:\([^\r\n)]+\))?: .+/im.test(message)) bump = 'minor'
  }
  return bump
}

export function incrementVersion(version, bump) {
  const [major, minor, patch] = versionParts(version)
  if (bump === 'major') return `${major + 1}.0.0`
  if (bump === 'minor') return `${major}.${minor + 1}.0`
  if (bump === 'patch') return `${major}.${minor}.${patch + 1}`
  throw new Error(`Incremento sconosciuto: ${bump}`)
}

/**
 * Versione dichiarata nel csproj: esattamente un elemento <Version> senza attributi (una <Version> condizionale
 * renderebbe ambigua la base), fuori dai commenti XML, con una SemVer stabile letterale.
 */
export function parseProjectVersion(text, source = projectFile) {
  const content = String(text).replace(/<!--[\s\S]*?-->/g, '')
  const matches = [...content.matchAll(/<Version(\s[^>]*)?>([^<]*)<\/Version\s*>/g)]
  if (matches.length !== 1)
    throw new Error(`${source}: atteso esattamente un elemento <Version>, trovati ${matches.length}.`)
  const [, attributes, value] = matches[0]
  if (attributes?.trim()) throw new Error(`${source}: <Version> non deve avere attributi o condizioni.`)
  const version = value.trim()
  if (!stableVersion.test(version))
    throw new Error(`${source}: <Version> non e' una SemVer stabile MAJOR.MINOR.PATCH: ${version}`)
  return version
}

export function readProjectVersion(cwd = process.cwd()) {
  let text
  try {
    text = readFileSync(resolve(cwd, projectFile), 'utf8')
  } catch (error) {
    throw new Error(`Impossibile leggere ${projectFile}: ${error.code ?? error.message}`)
  }
  return parseProjectVersion(text)
}

/** Pianificazione in sola lettura sulla cronologia Git reale; le release sono i record dell'API GitHub. */
export function planRelease({ cwd = process.cwd(), releases, sha = 'HEAD' }) {
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8' }).trim()
  const ancestor = (before, after) => {
    const result = spawnSync('git', ['merge-base', '--is-ancestor', before, after], { cwd })
    if (result.status !== 0 && result.status !== 1)
      throw new Error('Impossibile verificare la cronologia Git.')
    return result.status === 0
  }
  const head = git('rev-parse', `${sha}^{commit}`)
  const projectVersion = readProjectVersion(cwd)
  const tags = new Set(git('tag', '--list').split('\n'))
  const candidates = releases
    .filter((release) => !release.prerelease && /^v\d+\.\d+\.\d+$/.test(release.tag_name))
    .map((release) => ({ ...release, version: release.tag_name.slice(1) }))
    .sort((a, b) => compareVersions(b.version, a.version))
  const published = candidates
    .filter((release) => !release.draft)
    .map((release) => ({
      ...release,
      commit: git('rev-parse', `refs/tags/${release.tag_name}^{commit}`)
    }))

  // Una riesecuzione non deve mai creare un'altra versione ne' riportare Latest su codice piu' vecchio.
  const existing = published.find((release) => ancestor(head, release.commit))
  if (existing) return { skip: true, tag: existing.tag_name, sha: head }

  const previous = published[0]
  if (previous && !ancestor(previous.commit, head)) {
    throw new Error(
      `HEAD non discende da ${previous.tag_name}: ripristinare la cronologia di main prima del rilascio.`
    )
  }
  const range = previous ? `${previous.tag_name}..${head}` : head
  const records = git('log', '--reverse', '--format=%H%x00%B%x00', range).split('\0')
  const commits = []
  for (let index = 0; index + 1 < records.length; index += 2) {
    commits.push({ sha: records[index].trim(), message: records[index + 1].trim() })
  }
  const bump = getBump(commits.map((commit) => commit.message))
  const baseVersion =
    previous && compareVersions(previous.version, projectVersion) > 0
      ? previous.version
      : projectVersion
  const ownDraft = candidates.find((release) => release.draft && release.target_commitish === head)
  let version = ownDraft?.version ?? incrementVersion(baseVersion, bump)
  if (compareVersions(version, baseVersion) <= 0) {
    throw new Error('La bozza di questo commit precede la versione di base corrente.')
  }
  // Un upload interrotto puo' lasciare una versione riservata: il push successivo deve comunque rilasciare
  // tutte le modifiche dall'ultima release pubblicata, senza sovrascriverla.
  while (candidates.some((release) => release.tag_name === `v${version}` && release !== ownDraft)) {
    version = incrementVersion(version, 'patch')
  }
  const tag = `v${version}`
  const draft = candidates.find((release) => release.tag_name === tag && release.draft)
  if (tags.has(tag) && (!draft || git('rev-parse', `refs/tags/${tag}^{commit}`) !== head)) {
    throw new Error(`Il tag ${tag} esiste già e non è una bozza recuperabile di questo commit.`)
  }

  return {
    skip: false,
    sha: head,
    version,
    tag,
    baseVersion,
    bump,
    previousTag: previous?.tag_name ?? null,
    commits
  }
}

/** Nomi esatti degli eseguibili, gli stessi attesi da UpdateSource.AssetName nell'app. */
export function releaseAssetNames(version) {
  versionParts(version)
  return {
    frameworkDependent: `AIUsageMonitor-${version}-win-x64.exe`,
    selfContained: `AIUsageMonitor-${version}-win-x64-selfcontained.exe`
  }
}

/** Gli eseguibili in `directory`: esattamente le due varianti della versione calcolata, non vuote. */
export function collectArtifacts(directory, version) {
  const expected = Object.values(releaseAssetNames(version)).sort()
  const files = readdirSync(directory)
    .filter((name) => name.toLowerCase().endsWith('.exe'))
    .sort()
  if (files.length !== expected.length || files.some((name, index) => name !== expected[index])) {
    throw new Error(
      `Attesi esattamente ${expected.join(' e ')} in ${directory}; trovati: ${files.join(', ') || 'nessuno'}.`
    )
  }
  for (const name of files) {
    if (statSync(resolve(directory, name)).size === 0) throw new Error(`Artefatto vuoto: ${name}`)
  }
  return files
}

/** Manifest nel formato di `sha256sum` (`<hash>  <nome>`, una riga per file, LF), letto da ReleaseCatalog.ChecksumFor. */
export function checksumManifest(directory, files) {
  const lines = [...files].sort().map((name) => {
    const bytes = readFileSync(resolve(directory, name))
    if (bytes.length === 0) throw new Error(`Artefatto vuoto: ${name}`)
    return `${createHash('sha256').update(bytes).digest('hex')}  ${name}`
  })
  return `${lines.join('\n')}\n`
}

export function releaseNotes(plan, repository) {
  const assets = releaseAssetNames(plan.version)
  return [
    'AIUsageMonitor per Windows x64: eseguibile singolo, senza installazione.',
    '',
    `Versione **${plan.version}**: incremento **${plan.bump}** da ${plan.baseVersion}.`,
    `Commit: ${plan.sha}.`,
    '',
    '## Modifiche',
    '',
    ...plan.commits.map(
      (commit) => `- ${commit.message.split(/\r?\n/)[0]} (${commit.sha.slice(0, 7)})`
    ),
    '',
    ...(plan.previousTag
      ? [
          `[Confronto completo](https://github.com/${repository}/compare/${plan.previousTag}...${plan.tag})`,
          ''
        ]
      : []),
    '## Download',
    '',
    `- \`${assets.frameworkDependent}\`: variante framework-dependent, richiede il .NET 10 Desktop Runtime x64.`,
    `- \`${assets.selfContained}\`: variante self-contained, non richiede alcun runtime .NET installato (file più grande).`,
    `- \`${checksumManifestName}\`: checksum SHA-256 dei due eseguibili.`,
    '',
    '## Aggiornamento',
    '',
    'Dall\'app: Impostazioni > Aggiornamenti > "Collega GitHub e controlla". AIUsageMonitor scarica la stessa variante già in uso, ne verifica lo SHA-256 e si riavvia sulla nuova versione.',
    'In alternativa si può chiudere l\'app dalla tray e sostituire l\'eseguibile con quello nuovo della stessa variante.',
    'Gli aggiornamenti conservano le impostazioni (`%LOCALAPPDATA%\\AIUsageMonitor`) e gli hook già installati negli agenti (`%USERPROFILE%\\.aiusagemonitor`).',
    ''
  ].join('\n')
}

function gh(...args) {
  return execFileSync('gh', args, {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    maxBuffer: 64 * 1024 * 1024
  }).trim()
}

function listReleases() {
  const pages = JSON.parse(
    gh('api', '--paginate', '--slurp', `repos/${process.env.GH_REPO}/releases?per_page=100`)
  )
  return pages.flat()
}

function prepare() {
  const plan = planRelease({ releases: listReleases(), sha: process.env.GITHUB_SHA || 'HEAD' })
  writeFileSync(process.env.RELEASE_PLAN, `${JSON.stringify(plan, null, 2)}\n`)
  if (process.env.GITHUB_OUTPUT) {
    appendFileSync(
      process.env.GITHUB_OUTPUT,
      `skip=${plan.skip}\ntag=${plan.tag}\nversion=${plan.version ?? ''}\n`
    )
  }
  if (plan.skip) {
    console.log(`Commit già incluso in ${plan.tag}; nessun nuovo rilascio.`)
    return
  }

  // Nessun file del checkout viene modificato: la versione arriva a dotnet publish con -p:Version. Tag e release
  // restano l'autorita' sulla versione; niente commit del bot, scritture sul branch, PAT o build a catena.
  console.log(
    `${plan.baseVersion} -> ${plan.version} (${plan.bump}, ${plan.commits.length} commit)`
  )
}

function publish() {
  const plan = JSON.parse(readFileSync(process.env.RELEASE_PLAN, 'utf8'))
  if (plan.skip) return
  const directory = resolve('release')
  const files = collectArtifacts(directory, plan.version)
  writeFileSync(resolve(directory, checksumManifestName), checksumManifest(directory, files))
  const notesPath = `${process.env.RELEASE_PLAN}.md`
  writeFileSync(notesPath, releaseNotes(plan, process.env.GH_REPO))
  const existing = listReleases().find((release) => release.tag_name === plan.tag)
  if (existing && (!existing.draft || existing.target_commitish !== plan.sha)) {
    throw new Error(`${plan.tag} è già pubblicata o appartiene a un altro commit.`)
  }
  if (!existing) {
    gh(
      'release',
      'create',
      plan.tag,
      '--draft',
      '--target',
      plan.sha,
      '--title',
      plan.tag,
      '--notes-file',
      notesPath
    )
  } else {
    gh('release', 'edit', plan.tag, '--title', plan.tag, '--notes-file', notesPath)
  }
  const assets = [...files, checksumManifestName]
  gh('release', 'upload', plan.tag, ...assets.map((name) => resolve(directory, name)), '--clobber')
  const uploaded = JSON.parse(gh('release', 'view', plan.tag, '--json', 'assets')).assets
  for (const name of assets) {
    const asset = uploaded.find((item) => item.name === name)
    if (!asset || asset.size !== statSync(resolve(directory, name)).size) {
      throw new Error(`Upload incompleto: ${name}; la release resta in bozza.`)
    }
  }
  gh('release', 'edit', plan.tag, '--draft=false', '--latest')
  const url = `https://github.com/${process.env.GH_REPO}/releases/tag/${plan.tag}`
  if (process.env.GITHUB_STEP_SUMMARY)
    appendFileSync(process.env.GITHUB_STEP_SUMMARY, `Release pubblicata: [${plan.tag}](${url})\n`)
  console.log(url)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (!process.env.GH_REPO || !process.env.RELEASE_PLAN)
      throw new Error('GH_REPO e RELEASE_PLAN sono obbligatori.')
    if (process.argv[2] === 'prepare') prepare()
    else if (process.argv[2] === 'publish') publish()
    else throw new Error('Uso: node scripts/windows-release.mjs prepare|publish')
  } catch (error) {
    console.error(error.message)
    process.exitCode = 1
  }
}
