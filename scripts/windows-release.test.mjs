import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { after, before, describe, it } from 'node:test'
import {
  checksumManifest,
  collectArtifacts,
  compareVersions,
  getBump,
  incrementVersion,
  parseProjectVersion,
  planRelease,
  projectFile,
  readProjectVersion,
  releaseAssetNames,
  releaseNotes
} from './windows-release.mjs'

const csproj = (version) => `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <AssemblyName>AIUsageMonitor</AssemblyName>
    <Version>${version}</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Toolkit.Uwp.Notifications" Version="7.1.3" />
  </ItemGroup>
</Project>
`

describe('getBump', () => {
  it('maps a feat commit to a minor bump', () => {
    assert.equal(getBump(['feat: add the usage notch']), 'minor')
    assert.equal(getBump(['feat(tray): add the update entry']), 'minor')
  })

  it('maps a breaking marker to a major bump, whichever commit carries it', () => {
    assert.equal(getBump(['fix: typo', 'feat!: drop the legacy settings file']), 'major')
    assert.equal(getBump(['refactor(core)!: rename the hook events']), 'major')
    assert.equal(getBump(['chore: bump deps\n\nBREAKING CHANGE: settings move']), 'major')
    assert.equal(getBump(['chore: bump deps\n\nBREAKING-CHANGE: settings move']), 'major')
  })

  it('falls back to a patch bump', () => {
    assert.equal(getBump(['fix: notch flicker']), 'patch')
    assert.equal(getBump(['chore: tidy up', 'docs: readme']), 'patch')
    assert.equal(getBump([]), 'patch')
  })

  it('reads a header hidden in a squash commit body', () => {
    assert.equal(getBump(['chore: squash\n\nfeat: add the updater\nfix: guard']), 'minor')
  })

  it('does not treat a mention of feat inside a sentence as a feature', () => {
    assert.equal(getBump(['fix: handle the feat: prefix in titles']), 'patch')
  })
})

describe('incrementVersion', () => {
  it('increments and resets the lower fields', () => {
    assert.equal(incrementVersion('1.4.7', 'major'), '2.0.0')
    assert.equal(incrementVersion('1.4.7', 'minor'), '1.5.0')
    assert.equal(incrementVersion('1.4.7', 'patch'), '1.4.8')
  })

  it('rejects an unstable version or an unknown bump', () => {
    assert.throws(() => incrementVersion('1.4.7-rc.1', 'patch'))
    assert.throws(() => incrementVersion('01.4.7', 'patch'))
    assert.throws(() => incrementVersion('1.4.7', 'huge'))
  })
})

describe('compareVersions', () => {
  it('orders versions numerically', () => {
    assert.ok(compareVersions('0.2.0', '0.10.0') < 0)
    assert.ok(compareVersions('1.0.0', '0.99.99') > 0)
    assert.equal(compareVersions('1.0.0', '1.0.0'), 0)
  })

  it('rejects a version that is not stable SemVer', () => {
    assert.throws(() => compareVersions('1.0', '1.0.0'))
    assert.throws(() => compareVersions('1.0.0', 'v1.0.0'))
  })
})

describe('parseProjectVersion', () => {
  it('reads the <Version> property and ignores the Version attribute of package references', () => {
    assert.equal(parseProjectVersion(csproj('0.1.0')), '0.1.0')
    assert.equal(parseProjectVersion(csproj('12.3.45')), '12.3.45')
  })

  it('tolerates surrounding whitespace, CRLF line endings and a UTF-8 BOM', () => {
    const text = `﻿${csproj('\r\n      0.4.2\r\n    ').replace(/\n/g, '\r\n')}`
    assert.equal(parseProjectVersion(text), '0.4.2')
  })

  it('ignores other version properties and commented-out elements', () => {
    const text = csproj('0.1.0').replace(
      '<AssemblyName>',
      '<VersionPrefix>7.0.0</VersionPrefix><AssemblyVersion>8.0.0.0</AssemblyVersion>' +
        '<FileVersion>9.0.0.0</FileVersion><!-- <Version>5.0.0</Version> --><AssemblyName>'
    )
    assert.equal(parseProjectVersion(text), '0.1.0')
  })

  it('rejects a missing or duplicated <Version>', () => {
    assert.throws(() => parseProjectVersion('<Project><PropertyGroup /></Project>'), /trovati 0/)
    assert.throws(
      () =>
        parseProjectVersion(
          csproj('0.1.0').replace('</PropertyGroup>', '<Version>0.2.0</Version></PropertyGroup>')
        ),
      /trovati 2/
    )
  })

  it('rejects a conditional <Version>', () => {
    assert.throws(
      () =>
        parseProjectVersion(
          csproj('0.1.0').replace('<Version>', `<Version Condition="'$(CI)' == 'true'">`)
        ),
      /attributi/
    )
  })

  it('rejects a pre-release, metadata, leading zeros or an MSBuild expression', () => {
    for (const value of ['0.2.0-beta.1', '0.2.0+abc', '0.02.0', '1.0', '1.0.0.0', '$(BaseVersion)', '']) {
      assert.throws(() => parseProjectVersion(csproj(value)), /SemVer stabile/, value)
    }
  })

  it('names the project file in the error', () => {
    assert.throws(() => parseProjectVersion(csproj('dev')), new RegExp(projectFile.replace(/\./g, '\\.')))
  })

  it('reads the App project of a checkout and explains a missing file', () => {
    const dir = mkdtempSync(join(tmpdir(), 'aiusagemonitor-csproj-'))
    try {
      assert.throws(() => readProjectVersion(dir), /Impossibile leggere/)
      mkdirSync(join(dir, 'src', 'AIUsageMonitor.App'), { recursive: true })
      writeFileSync(join(dir, projectFile), csproj('3.2.1'))
      assert.equal(readProjectVersion(dir), '3.2.1')
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('matches the <Version> committed in this repository', () => {
    const version = readProjectVersion(join(import.meta.dirname, '..'))
    assert.match(version, /^\d+\.\d+\.\d+$/)
  })
})

describe('release artifacts', () => {
  let dir = ''

  before(() => {
    dir = mkdtempSync(join(tmpdir(), 'aiusagemonitor-artifacts-'))
  })

  after(() => {
    rmSync(dir, { recursive: true, force: true })
  })

  it('names the two variants exactly as the updater looks them up', () => {
    assert.deepEqual(releaseAssetNames('1.2.3'), {
      frameworkDependent: 'AIUsageMonitor-1.2.3-win-x64.exe',
      selfContained: 'AIUsageMonitor-1.2.3-win-x64-selfcontained.exe'
    })
    assert.throws(() => releaseAssetNames('1.2.3-rc.1'))
  })

  it('requires exactly the two executables of the planned version', () => {
    assert.throws(() => collectArtifacts(dir, '1.2.3'), /nessuno/)
    writeFileSync(join(dir, 'AIUsageMonitor-1.2.3-win-x64.exe'), 'MZ framework-dependent')
    assert.throws(() => collectArtifacts(dir, '1.2.3'), /Attesi esattamente/)
    writeFileSync(join(dir, 'AIUsageMonitor-1.2.3-win-x64-selfcontained.exe'), 'MZ self-contained')
    assert.deepEqual(collectArtifacts(dir, '1.2.3'), [
      'AIUsageMonitor-1.2.3-win-x64-selfcontained.exe',
      'AIUsageMonitor-1.2.3-win-x64.exe'
    ])
    assert.throws(() => collectArtifacts(dir, '1.2.4'), /Attesi esattamente/)
    writeFileSync(join(dir, 'AIUsageMonitor.exe'), 'MZ stray')
    assert.throws(() => collectArtifacts(dir, '1.2.3'), /Attesi esattamente/)
    rmSync(join(dir, 'AIUsageMonitor.exe'))
    writeFileSync(join(dir, 'AIUsageMonitor-1.2.3-win-x64.exe'), '')
    assert.throws(() => collectArtifacts(dir, '1.2.3'), /vuoto/)
    writeFileSync(join(dir, 'AIUsageMonitor-1.2.3-win-x64.exe'), 'MZ framework-dependent')
  })

  it('writes a sha256sum manifest with one LF-terminated line per executable', () => {
    const files = collectArtifacts(dir, '1.2.3')
    const manifest = checksumManifest(dir, files)
    const hash = (name) =>
      createHash('sha256').update(readFileSync(join(dir, name))).digest('hex')
    assert.equal(
      manifest,
      `${hash(files[0])}  ${files[0]}\n${hash(files[1])}  ${files[1]}\n`
    )
    // Stessa espressione di ReleaseCatalog.ChecksumLine nell'app.
    for (const line of manifest.trimEnd().split('\n')) {
      assert.match(line, /^[a-f0-9]{64}\s+\*?(.+)$/)
    }
  })

  it('writes Italian release notes listing both variants, the commits and the comparison', () => {
    const notes = releaseNotes(
      {
        version: '0.3.0',
        tag: 'v0.3.0',
        baseVersion: '0.2.4',
        bump: 'minor',
        sha: 'a'.repeat(40),
        previousTag: 'v0.2.4',
        commits: [
          { sha: 'b'.repeat(40), message: 'feat: add the updater\n\nCorpo del commit.' },
          { sha: 'c'.repeat(40), message: 'fix: notch flicker' }
        ]
      },
      'owner/AIUsageMonitor'
    )
    assert.match(notes, /Versione \*\*0\.3\.0\*\*: incremento \*\*minor\*\* da 0\.2\.4\./)
    assert.match(notes, /^- feat: add the updater \(bbbbbbb\)$/m)
    assert.match(notes, /^- fix: notch flicker \(ccccccc\)$/m)
    assert.doesNotMatch(notes, /Corpo del commit/)
    assert.match(notes, /compare\/v0\.2\.4\.\.\.v0\.3\.0/)
    assert.match(notes, /`AIUsageMonitor-0\.3\.0-win-x64\.exe`: .*\.NET 10 Desktop Runtime/)
    assert.match(notes, /`AIUsageMonitor-0\.3\.0-win-x64-selfcontained\.exe`: .*self-contained/)
    assert.match(notes, /Impostazioni > Aggiornamenti > "Collega GitHub e controlla"/)
    assert.match(notes, /SHA256SUMS\.txt/)
    const first = releaseNotes(
      { version: '0.2.0', tag: 'v0.2.0', baseVersion: '0.1.0', bump: 'minor', sha: 'a'.repeat(40), previousTag: null, commits: [] },
      'owner/AIUsageMonitor'
    )
    assert.doesNotMatch(first, /Confronto completo/)
  })
})

// Un solo repository usa-e-getta serve tutti gli scenari: su Windows git e' abbastanza lento da rendere dominante la
// ricostruzione della fixture per ogni test. planRelease e' in sola lettura, quindi gli scenari differiscono solo per
// lo `sha` da cui si pianifica e per le release passate (il csproj viene riscritto e ripristinato da un solo test).
describe('planRelease', () => {
  const PUBLISHED_V011 = { tag_name: 'v0.1.1', draft: false, prerelease: false }
  let repo = ''
  let sha = {}

  before(() => {
    repo = mkdtempSync(join(tmpdir(), 'aiusagemonitor-release-'))
    const git = (...args) =>
      execFileSync(
        'git',
        ['-C', repo, '-c', 'commit.gpgsign=false', '-c', 'tag.gpgsign=false', ...args],
        {
          encoding: 'utf8',
          env: {
            ...process.env,
            GIT_AUTHOR_NAME: 'AIUsageMonitor Test',
            GIT_AUTHOR_EMAIL: 'test@example.invalid',
            GIT_COMMITTER_NAME: 'AIUsageMonitor Test',
            GIT_COMMITTER_EMAIL: 'test@example.invalid'
          }
        }
      ).trim()
    execFileSync('git', ['init', '-q', '--initial-branch=main', repo], { encoding: 'utf8' })
    mkdirSync(join(repo, 'src', 'AIUsageMonitor.App'), { recursive: true })
    writeFileSync(join(repo, projectFile), csproj('0.1.0'))
    git('add', '--', projectFile)
    git('commit', '-q', '-m', 'chore: initial import')
    // Commit vuoti per tenere veloce la fixture: si leggono solo la cronologia e il csproj.
    const commit = (message) => git('commit', '-q', '--allow-empty', '-m', message)
    commit('fix: first fix')
    git('tag', 'v0.1.1')
    commit('feat: second feature')
    commit('fix: third fix')
    git('checkout', '-q', '-b', 'divergent', 'v0.1.1~1')
    commit('fix: parallel work')
    const history = git('log', '--format=%H %s', '--all').split('\n')
    const find = (subject) => history.find((line) => line.endsWith(subject)).split(' ')[0]
    sha = {
      initial: find('chore: initial import'),
      firstFix: find('fix: first fix'),
      secondFeature: find('feat: second feature'),
      thirdFix: find('fix: third fix'),
      divergent: find('fix: parallel work')
    }
  })

  after(() => {
    // Su Windows gli oggetti Git sono in sola lettura e un antivirus puo' tenerli aperti per un attimo.
    rmSync(repo, { recursive: true, force: true, maxRetries: 5 })
  })

  it('plans the first release from the csproj version', () => {
    const plan = planRelease({ cwd: repo, releases: [], sha: sha.secondFeature })
    assert.equal(plan.skip, false)
    assert.equal(plan.sha, sha.secondFeature)
    assert.equal(plan.baseVersion, '0.1.0')
    assert.equal(plan.bump, 'minor')
    assert.equal(plan.version, '0.2.0')
    assert.equal(plan.tag, 'v0.2.0')
    assert.equal(plan.previousTag, null)
    assert.deepEqual(
      plan.commits.map((entry) => entry.sha),
      [sha.initial, sha.firstFix, sha.secondFeature]
    )
  })

  it('skips when HEAD is already contained in a published release', () => {
    const plan = planRelease({ cwd: repo, releases: [PUBLISHED_V011], sha: sha.firstFix })
    assert.deepEqual(plan, { skip: true, tag: 'v0.1.1', sha: sha.firstFix })
    const older = planRelease({ cwd: repo, releases: [PUBLISHED_V011], sha: sha.initial })
    assert.deepEqual(older, { skip: true, tag: 'v0.1.1', sha: sha.initial })
  })

  it('counts only the commits after the last published tag', () => {
    const plan = planRelease({ cwd: repo, releases: [PUBLISHED_V011], sha: sha.thirdFix })
    assert.equal(plan.skip, false)
    assert.equal(plan.baseVersion, '0.1.1')
    assert.equal(plan.bump, 'minor')
    assert.equal(plan.version, '0.2.0')
    assert.equal(plan.previousTag, 'v0.1.1')
    assert.deepEqual(
      plan.commits.map((entry) => entry.message),
      ['feat: second feature', 'fix: third fix']
    )
  })

  it('throws when HEAD does not descend from the last published release', () => {
    assert.throws(
      () => planRelease({ cwd: repo, releases: [PUBLISHED_V011], sha: sha.divergent }),
      /non discende/
    )
  })

  it('skips a version already reserved by another release', () => {
    const plan = planRelease({
      cwd: repo,
      releases: [
        PUBLISHED_V011,
        { tag_name: 'v0.2.0', draft: true, prerelease: false, target_commitish: 'another-commit' }
      ],
      sha: sha.thirdFix
    })
    assert.equal(plan.version, '0.2.1')
    assert.equal(plan.tag, 'v0.2.1')
  })

  it('resumes the draft left by an interrupted run of the same commit', () => {
    const plan = planRelease({
      cwd: repo,
      releases: [
        PUBLISHED_V011,
        { tag_name: 'v0.2.0', draft: true, prerelease: false, target_commitish: sha.thirdFix }
      ],
      sha: sha.thirdFix
    })
    assert.equal(plan.version, '0.2.0')
    assert.equal(plan.tag, 'v0.2.0')
  })

  it('accepts an existing tag only for the recoverable draft of this commit', () => {
    const draft = (target) => ({ tag_name: 'v0.1.1', draft: true, prerelease: false, target_commitish: target })
    const plan = planRelease({ cwd: repo, releases: [draft(sha.firstFix)], sha: sha.firstFix })
    assert.equal(plan.skip, false)
    assert.equal(plan.version, '0.1.1')
    assert.throws(
      () => planRelease({ cwd: repo, releases: [draft(sha.secondFeature)], sha: sha.secondFeature }),
      /Il tag v0\.1\.1 esiste già/
    )
  })

  it('throws when the computed tag already exists without a release', () => {
    assert.throws(
      () => planRelease({ cwd: repo, releases: [], sha: sha.firstFix }),
      /Il tag v0\.1\.1 esiste già/
    )
  })

  it('refuses a draft of this commit that does not move past the base version', () => {
    assert.throws(
      () =>
        planRelease({
          cwd: repo,
          releases: [{ tag_name: 'v0.1.0', draft: true, prerelease: false, target_commitish: sha.initial }],
          sha: sha.initial
        }),
      /precede la versione di base/
    )
  })

  it('ignores pre-releases and tags that are not stable versions', () => {
    const plan = planRelease({
      cwd: repo,
      releases: [
        { tag_name: 'v9.0.0', draft: false, prerelease: true },
        { tag_name: 'v1.0.0-rc.1', draft: false, prerelease: false },
        { tag_name: 'nightly', draft: false, prerelease: false }
      ],
      sha: sha.secondFeature
    })
    assert.equal(plan.version, '0.2.0')
    assert.equal(plan.previousTag, null)
  })

  it('uses the csproj version as the base when it is ahead of the last release', () => {
    const path = join(repo, projectFile)
    const original = readFileSync(path, 'utf8')
    try {
      writeFileSync(path, csproj('1.0.0'))
      const plan = planRelease({ cwd: repo, releases: [PUBLISHED_V011], sha: sha.thirdFix })
      assert.equal(plan.baseVersion, '1.0.0')
      assert.equal(plan.version, '1.1.0')
      assert.equal(plan.previousTag, 'v0.1.1')
      writeFileSync(path, csproj('1.0.0-preview.1'))
      assert.throws(
        () => planRelease({ cwd: repo, releases: [PUBLISHED_V011], sha: sha.thirdFix }),
        /SemVer stabile/
      )
    } finally {
      writeFileSync(path, original)
    }
  })
})
