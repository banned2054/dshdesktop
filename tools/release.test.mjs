import assert from 'node:assert/strict'
import { cpSync, existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { readVersions, smoke, stage, verifyBundle, verifyTree } from './release.mjs'

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'dsh-packaging-test-'))
  t.after(() => rmSync(root, { recursive: true, force: true }))
  return root
}

function write(root, path, content) {
  const destination = join(root, path)
  mkdirSync(join(destination, '..'), { recursive: true })
  writeFileSync(destination, typeof content === 'string' ? content : JSON.stringify(content))
}

function project(root) {
  write(root, 'backend-version.json', { version: '0.2.0-rc.2', ref: 'dsh-v0.2.0-rc.2', commit: 'a'.repeat(40) })
  write(root, 'DshDesktop/DshDesktop.csproj', '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>')
  write(root, 'LICENSE', 'client license')
  write(root, 'NOTICE', 'notices')
}

const targetDetails = {
  'win-x64': { executable: 'DshDesktop.exe', node: 'node.exe', platform: 'win32', arch: 'x64' },
  'osx-arm64': { executable: 'DshDesktop', node: 'node', platform: 'darwin', arch: 'arm64' },
  'linux-x64': { executable: 'DshDesktop', node: 'node', platform: 'linux', arch: 'x64' },
}

function resources(root, rid) {
  return rid === 'osx-arm64' ? join(root, 'DshDesktop.app/Contents/Resources') : root
}

function executable(root, rid) {
  return rid === 'osx-arm64' ? join(root, 'DshDesktop.app/Contents/MacOS/DshDesktop') : join(root, targetDetails[rid].executable)
}

function bundle(root, rid = 'win-x64') {
  const target = targetDetails[rid]
  const rootResources = resources(root, rid)
  for (const path of [
    'Assets/Backend/launcher.mjs',
    `backend/primary-runtime/dependencies/node/bin/${target.node}`,
    'backend/primary-runtime/dependencies/pnpm/bin/pnpm.mjs',
    'backend/office-skills/scripts/check_office.py',
  ]) write(rootResources, path, 'fixture')
  write(rootResources, 'backend/primary-runtime/runtime.json', { desktopVersion: '0.2.0-rc.2', node: '24.21.0', platform: target.platform, arch: target.arch })
  write(root, rid === 'osx-arm64' ? 'DshDesktop.app/Contents/MacOS/DshDesktop' : target.executable, 'fixture')
  if (rid === 'osx-arm64') {
    write(root, 'DshDesktop.app/Contents/MacOS/DshDesktop.runtimeconfig.json', '{}')
    write(root, 'DshDesktop.app/Contents/Info.plist', '<plist/>')
  }
}

test('client and DSH have independent versions; release tags must match client', t => {
  const root = fixture(t)
  project(root)
  assert.equal(readVersions(root, 'v0.1.0').appVersion, '0.1.0')
  assert.equal(readVersions(root).version, '0.2.0-rc.2')
  assert.throws(() => readVersions(root, 'v0.2.0'), /Release tag/)
})

test('moving references and abbreviated commits are rejected', t => {
  const root = fixture(t)
  project(root)
  write(root, 'backend-version.json', { version: '0.2.0-rc.2', ref: 'master', commit: 'a'.repeat(40) })
  assert.throws(() => readVersions(root), /ref must match/)
  write(root, 'backend-version.json', { version: '0.2.0-rc.2', ref: 'dsh-v0.2.0-rc.2', commit: 'abcdef' })
  assert.throws(() => readVersions(root), /full SHA/)
})

test('filesystem links cannot enter the final archive tree', t => {
  const root = fixture(t)
  const target = join(root, 'target')
  mkdirSync(target)
  symlinkSync(target, join(root, 'link'), process.platform === 'win32' ? 'junction' : 'dir')
  assert.throws(() => verifyTree(root), /filesystem link/)
})

test('staging materializes linked dependencies beside a directly launchable app', t => {
  const root = fixture(t)
  const repo = join(root, 'repo')
  const source = join(root, 'upstream')
  const deploy = join(root, 'deploy')
  const output = join(root, 'output')
  project(repo)
  bundle(output)
  write(source, 'LICENSE', 'upstream license')
  write(deploy, 'package.json', { name: '@deepseek-ai/dsh-desktop-host', version: '0.2.0-rc.2' })
  write(deploy, 'lib/index.js', 'fixture host')
  const dsh = join(root, 'linked-dsh')
  write(dsh, 'package.json', { name: '@deepseek-ai/dsh', version: '0.2.0-rc.2' })
  write(dsh, 'lib/profile-boot.js', 'fixture boot')
  mkdirSync(join(deploy, 'node_modules/@deepseek-ai'), { recursive: true })
  symlinkSync(dsh, join(deploy, 'node_modules/@deepseek-ai/dsh'), process.platform === 'win32' ? 'junction' : 'dir')
  stage(source, deploy, output, repo)
  rmSync(dsh, { recursive: true })
  verifyBundle(output, readVersions(repo))
  assert.equal(readFileSync(join(output, 'DshDesktop.exe'), 'utf8'), 'fixture')
  assert.equal(JSON.parse(readFileSync(join(output, 'BUILD-INFO.json'))).clientVersion, '0.1.0')
  assert.equal(existsSync(join(output, 'Run.cmd')), false)
  assert.throws(() => stage(source, deploy, output, repo), /must be new/)
})

test('staging creates a macOS app bundle and a direct Linux executable', t => {
  const root = fixture(t)
  const repo = join(root, 'repo')
  const source = join(root, 'upstream')
  const deploy = join(root, 'deploy')
  project(repo)
  write(source, 'LICENSE', 'upstream license')
  write(deploy, 'package.json', { name: '@deepseek-ai/dsh-desktop-host', version: '0.2.0-rc.2' })
  write(deploy, 'lib/index.js', 'fixture host')
  for (const rid of ['osx-arm64', 'linux-x64']) {
    const output = join(root, rid)
    mkdirSync(output)
    bundle(output, rid)
    const dsh = join(root, `${rid}-dsh`)
    write(dsh, 'package.json', { name: '@deepseek-ai/dsh', version: '0.2.0-rc.2' })
    write(dsh, 'lib/profile-boot.js', 'fixture boot')
    mkdirSync(join(deploy, 'node_modules/@deepseek-ai'), { recursive: true })
    symlinkSync(dsh, join(deploy, `node_modules/@deepseek-ai/dsh-${rid}`), process.platform === 'win32' ? 'junction' : 'dir')
    rmSync(join(deploy, 'node_modules/@deepseek-ai/dsh-' + rid))
    symlinkSync(dsh, join(deploy, 'node_modules/@deepseek-ai/dsh'), process.platform === 'win32' ? 'junction' : 'dir')
    stage(source, deploy, output, repo, rid)
    assert.equal(existsSync(join(output, 'Run.command')), false)
    assert.equal(existsSync(join(output, 'Run.sh')), false)
    if (rid === 'osx-arm64') {
      assert.equal(existsSync(join(output, 'DshDesktop.app/Contents/MacOS/DshDesktop')), true)
      assert.equal(existsSync(join(output, 'DshDesktop.app/Contents/MacOS/DshDesktop.runtimeconfig.json')), true)
      assert.match(readFileSync(join(output, 'DshDesktop.app/Contents/Info.plist'), 'utf8'), /CFBundlePackageType/)
    } else {
      assert.equal(existsSync(join(output, 'DshDesktop')), true)
    }
    assert.equal(JSON.parse(readFileSync(join(resources(output, rid), 'BUILD-INFO.json'))).platform, rid)
    rmSync(join(deploy, 'node_modules/@deepseek-ai/dsh'))
  }
})

test('bundle validation accepts macOS arm64 app and Linux x64 runtime manifests', t => {
  const root = fixture(t)
  project(root)
  for (const rid of ['osx-arm64', 'linux-x64']) {
    const output = join(root, rid)
    mkdirSync(output)
    bundle(output, rid)
    const rootResources = resources(output, rid)
    write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.2.0-rc.2' })
    write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/package.json', { version: '0.2.0-rc.2' })
    write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture')
    write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', 'fixture')
    verifyBundle(output, readVersions(root), rid)
  }
  assert.throws(() => verifyBundle(join(root, 'osx-arm64'), readVersions(root), 'linux-x64'), /Bundle is missing/)
})

test('bundle validation rejects version drift and missing backend launcher asset', t => {
  const root = fixture(t)
  project(root)
  bundle(root)
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.1.7-rc.2' })
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/package.json', { version: '0.2.0-rc.2' })
  assert.throws(() => verifyBundle(root, readVersions(root)), /differs from pin/)
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.2.0-rc.2' })
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture')
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', 'fixture')
  rmSync(join(resources(root, 'win-x64'), 'Assets/Backend/launcher.mjs'))
  assert.throws(() => verifyBundle(root, readVersions(root)), /missing Assets/)
})

test('packaged smoke observes readiness and clean shutdown with bundled Node', { skip: !['win32', 'darwin', 'linux'].includes(process.platform) }, async t => {
  const root = fixture(t)
  const rid = process.platform === 'win32' ? 'win-x64' : process.platform === 'darwin' ? 'osx-arm64' : 'linux-x64'
  bundle(root, rid)
  const rootResources = resources(root, rid)
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.2.0-rc.2' })
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture')
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/package.json', { version: '0.2.0-rc.2', type: 'module' })
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    process.send({ type: 'ready', url: 'http://127.0.0.1:19387/?token=fixture' })
    process.on('message', message => {
      if (message.type === 'shutdown') {
        process.send({ type: 'shutdown-complete' }, () => process.disconnect())
      }
    })
  `)
  cpSync(process.execPath, join(rootResources, 'backend/primary-runtime/dependencies/node/bin', targetDetails[rid].node))
  cpSync('DshDesktop.Infrastructure/Assets/Backend/launcher.mjs', join(rootResources, 'Assets/Backend/launcher.mjs'))
  await smoke(root, rid)
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    process.send({ type: 'fatal', message: 'fixture startup failure' }, () => process.exit(1))
  `)
  await assert.rejects(() => smoke(root, rid), /Invalid launcher control frame/)
})
