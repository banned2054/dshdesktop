import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { cpSync, existsSync, lstatSync, mkdtempSync, mkdirSync, readFileSync, readlinkSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, relative } from 'node:path'
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

function directoryLink(target, path, relativeTarget = false) {
  // Windows junctions do not exercise the POSIX symlink copy path.
  const type = process.platform === 'win32' ? 'junction' : 'dir'
  const linkTarget = relativeTarget && type !== 'junction' ? relative(dirname(path), target) : target
  symlinkSync(linkTarget, path, type)
  assert.equal(lstatSync(path).isSymbolicLink(), true)
  assert.equal(lstatSync(path).isDirectory(), false)
  assert.equal(realpathSync(path), realpathSync(target))
  if (type === 'dir') assert.equal(readlinkSync(path), linkTarget)
}

function materializedDirectory(path) {
  assert.equal(lstatSync(path).isSymbolicLink(), false)
  assert.equal(lstatSync(path).isDirectory(), true)
}

function materializedFile(path, content) {
  assert.equal(lstatSync(path).isSymbolicLink(), false)
  assert.equal(lstatSync(path).isFile(), true)
  assert.equal(readFileSync(path, 'utf8'), content)
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

function bundle(root, rid = 'win-x64', flat = false) {
  const target = targetDetails[rid]
  const rootResources = flat ? root : resources(root, rid)
  for (const path of [
    'Assets/Backend/launcher.mjs',
    `backend/primary-runtime/dependencies/node/bin/${target.node}`,
    'backend/primary-runtime/dependencies/pnpm/bin/pnpm.mjs',
    'backend/office-skills/scripts/check_office.py',
  ]) write(rootResources, path, 'fixture')
  write(rootResources, 'backend/primary-runtime/runtime.json', { desktopVersion: '0.2.0-rc.2', node: '24.21.0', platform: target.platform, arch: target.arch })
  write(root, rid === 'osx-arm64' && !flat ? 'DshDesktop.app/Contents/MacOS/DshDesktop' : target.executable, 'fixture')
  if (rid === 'osx-arm64' && !flat) {
    write(root, 'DshDesktop.app/Contents/Info.plist', '<plist/>')
  }
}

test('release prepares runtime before production deploy and runs no pnpm scripts afterward', () => {
  const script = readFileSync(new URL('./build-release.ps1', import.meta.url), 'utf8')
  const commands = [...script.matchAll(/^\s*pnpm\s+(.+)$/gm)].map(match => match[1].trim())
  const prepare = commands.findIndex(command => command.startsWith('run prepare:primary-runtime '))
  const deploy = commands.findIndex(command => command.startsWith('--filter @deepseek-ai/dsh-desktop-host deploy --legacy --prod '))
  assert.notEqual(prepare, -1, 'Runtime preparation command is required')
  assert.notEqual(deploy, -1, 'Production dependency export command is required')
  assert.ok(prepare < deploy, 'Runtime preparation must precede production-only workspace state')
  assert.equal(commands.slice(deploy + 1).some(command => /^run\s/.test(command)), false)
})

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
  directoryLink(target, join(root, 'link'))
  assert.throws(() => verifyTree(root), /filesystem link/)
})

test('filesystem file links and dangling links are rejected', { skip: process.platform === 'win32' }, t => {
  const root = fixture(t)
  write(root, 'target.txt', 'linked content')
  const link = join(root, 'link.txt')
  symlinkSync('target.txt', link, 'file')
  assert.equal(lstatSync(link).isSymbolicLink(), true)
  assert.equal(readlinkSync(link), 'target.txt')
  assert.equal(readFileSync(link, 'utf8'), 'linked content')
  assert.throws(() => verifyTree(root), /filesystem link/)
  rmSync(join(root, 'target.txt'))
  assert.equal(existsSync(link), false)
  assert.equal(lstatSync(link).isSymbolicLink(), true)
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
  const linkedAssets = join(root, 'linked-assets')
  write(linkedAssets, 'data.txt', 'nested linked content')
  directoryLink(linkedAssets, join(dsh, 'lib/assets'), true)
  directoryLink(linkedAssets, join(deploy, 'lib/assets'), true)
  if (process.platform !== 'win32') {
    symlinkSync('profile-boot.js', join(dsh, 'lib/boot-alias.js'), 'file')
    symlinkSync('index.js', join(deploy, 'lib/host-alias.js'), 'file')
    assert.equal(lstatSync(join(dsh, 'lib/boot-alias.js')).isSymbolicLink(), true)
    assert.equal(readlinkSync(join(dsh, 'lib/boot-alias.js')), 'profile-boot.js')
    assert.equal(lstatSync(join(deploy, 'lib/host-alias.js')).isSymbolicLink(), true)
    assert.equal(readlinkSync(join(deploy, 'lib/host-alias.js')), 'index.js')
  }
  mkdirSync(join(deploy, 'node_modules/@deepseek-ai'), { recursive: true })
  const dependency = join(deploy, 'node_modules/@deepseek-ai/dsh')
  directoryLink(dsh, dependency)
  assert.equal(readFileSync(join(dependency, 'lib/profile-boot.js'), 'utf8'), 'fixture boot')
  stage(source, deploy, output, repo)
  rmSync(dsh, { recursive: true })
  rmSync(linkedAssets, { recursive: true })
  rmSync(join(deploy, 'node_modules'), { recursive: true })
  rmSync(join(deploy, 'lib'), { recursive: true })
  verifyBundle(output, readVersions(repo))
  const packages = join(output, 'backend/runtime/node_modules/@deepseek-ai')
  for (const name of ['dsh', 'dsh-desktop-host']) {
    materializedDirectory(join(packages, name))
    materializedDirectory(join(packages, name, 'lib/assets'))
    materializedFile(join(packages, name, 'lib/assets/data.txt'), 'nested linked content')
  }
  materializedFile(join(packages, 'dsh/lib/profile-boot.js'), 'fixture boot')
  materializedFile(join(packages, 'dsh-desktop-host/lib/index.js'), 'fixture host')
  if (process.platform !== 'win32') {
    materializedFile(join(packages, 'dsh/lib/boot-alias.js'), 'fixture boot')
    materializedFile(join(packages, 'dsh-desktop-host/lib/host-alias.js'), 'fixture host')
  }
  assert.equal(readFileSync(join(output, 'DshDesktop.exe'), 'utf8'), 'fixture')
  assert.equal(JSON.parse(readFileSync(join(output, 'BUILD-INFO.json'))).clientVersion, '0.1.0')
  assert.equal(existsSync(join(output, 'Run.cmd')), false)
  assert.throws(() => stage(source, deploy, output, repo), /must be new/)
})

test('staging preserves hoisted dependency cycles and nested versions after relocation', t => {
  const root = fixture(t)
  const repo = join(root, 'repo')
  const source = join(root, 'upstream')
  const deploy = join(root, 'deploy')
  const output = join(root, 'output')
  project(repo)
  bundle(output)
  write(source, 'LICENSE', 'upstream license')
  write(deploy, 'package.json', { name: '@deepseek-ai/dsh-desktop-host', version: '0.2.0-rc.2', type: 'module' })
  write(deploy, 'lib/index.js', 'fixture host')
  write(deploy, 'node_modules/@deepseek-ai/dsh/package.json', { name: '@deepseek-ai/dsh', version: '0.2.0-rc.2' })
  write(deploy, 'node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture boot')
  write(deploy, 'node_modules/cordis/package.json', { name: 'cordis', main: 'index.cjs', dependencies: { 'cordis-plugin-include': '*', shared: '2.0.0' } })
  write(deploy, 'node_modules/cordis/index.cjs', `exports.name = 'cordis'; exports.include = () => require('cordis-plugin-include')`)
  write(deploy, 'node_modules/cordis-plugin-include/package.json', { name: 'cordis-plugin-include', main: 'index.cjs', dependencies: { cordis: '*', shared: '1.0.0' } })
  write(deploy, 'node_modules/cordis-plugin-include/index.cjs', `exports.cordis = require('cordis'); exports.version = require('shared').version`)
  write(deploy, 'node_modules/shared/package.json', { name: 'shared', main: 'index.cjs', version: '1.0.0' })
  write(deploy, 'node_modules/shared/index.cjs', `exports.version = '1.0.0'`)
  write(deploy, 'node_modules/cordis/node_modules/shared/package.json', { name: 'shared', main: 'index.cjs', version: '2.0.0' })
  write(deploy, 'node_modules/cordis/node_modules/shared/index.cjs', `exports.version = '2.0.0'`)
  stage(source, deploy, output, repo)
  const moved = join(root, 'relocated package')
  cpSync(output, moved, { recursive: true })
  rmSync(output, { recursive: true })
  rmSync(deploy, { recursive: true })
  verifyBundle(moved, readVersions(repo))
  const host = join(moved, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js')
  const result = execFileSync(process.execPath, ['--input-type=module', '-e', `
    import { createRequire } from 'node:module'
    const require = createRequire(process.argv[1])
    const cordis = require('cordis')
    const fromCordis = createRequire(require.resolve('cordis'))
    console.log(JSON.stringify({ cycle: cordis.include().cordis === cordis,
      hoisted: cordis.include().version, nested: fromCordis('shared').version }))
  `, host], { encoding: 'utf8', env: { ...process.env, NODE_PATH: '', NODE_OPTIONS: '' } })
  assert.deepEqual(JSON.parse(result), { cycle: true, hoisted: '1.0.0', nested: '2.0.0' })
})

test('staging materializes pnpm virtual-store cycles without copying the store or losing versions', t => {
  const root = fixture(t)
  const repo = join(root, 'repo')
  const source = join(root, 'upstream')
  const deploy = join(root, 'deploy')
  const output = join(root, 'output')
  const modules = join(deploy, 'node_modules')
  const store = join(modules, '.pnpm')
  const cordis = join(store, 'cordis/node_modules/cordis')
  const include = join(store, 'include/node_modules/cordis-plugin-include')
  const shared1 = join(store, 'shared1/node_modules/shared')
  const shared2 = join(store, 'shared2/node_modules/shared')
  project(repo)
  bundle(output)
  write(source, 'LICENSE', 'upstream license')
  write(deploy, 'package.json', { name: '@deepseek-ai/dsh-desktop-host', version: '0.2.0-rc.2', dependencies: { cordis: '*' } })
  write(deploy, 'lib/index.js', `module.exports = require('cordis')`)
  write(deploy, 'node_modules/@deepseek-ai/dsh/package.json', { name: '@deepseek-ai/dsh', version: '0.2.0-rc.2' })
  write(deploy, 'node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture boot')
  write(cordis, 'package.json', { name: 'cordis', main: 'index.cjs', dependencies: { 'cordis-plugin-include': '*', shared: '1.0.0' } })
  write(cordis, 'index.cjs', `exports.name = 'cordis'; exports.include = () => require('cordis-plugin-include'); exports.version = require('shared').version`)
  write(include, 'package.json', { name: 'cordis-plugin-include', main: 'index.cjs', dependencies: { cordis: '*', shared: '2.0.0' }, peerDependencies: { '@deepseek-ai/dsh-desktop-host': '*' } })
  write(include, 'index.cjs', `exports.cordis = require('cordis'); exports.host = () => require('@deepseek-ai/dsh-desktop-host/lib/index.js'); exports.version = require('shared').version`)
  for (const [path, version] of [[shared1, '1.0.0'], [shared2, '2.0.0']]) {
    write(path, 'package.json', { name: 'shared', main: 'index.cjs', version })
    write(path, 'index.cjs', `exports.version = '${version}'`)
  }
  directoryLink(cordis, join(modules, 'cordis'), true)
  directoryLink(include, join(store, 'cordis/node_modules/cordis-plugin-include'), true)
  directoryLink(shared1, join(store, 'cordis/node_modules/shared'), true)
  directoryLink(cordis, join(store, 'include/node_modules/cordis'), true)
  directoryLink(shared2, join(store, 'include/node_modules/shared'), true)
  mkdirSync(join(cordis, 'node_modules'), { recursive: true })
  directoryLink(include, join(cordis, 'node_modules/cordis-plugin-include'), true)
  mkdirSync(join(include, 'node_modules'), { recursive: true })
  directoryLink(cordis, join(include, 'node_modules/cordis'), true)
  mkdirSync(join(store, 'node_modules/@deepseek-ai'), { recursive: true })
  directoryLink(deploy, join(store, 'node_modules/@deepseek-ai/dsh-desktop-host'), true)
  stage(source, deploy, output, repo)
  rmSync(deploy, { recursive: true })
  const moved = join(root, 'isolated extracted package')
  cpSync(output, moved, { recursive: true })
  rmSync(output, { recursive: true })
  verifyBundle(moved, readVersions(repo))
  assert.equal(existsSync(join(moved, 'backend/runtime/node_modules/.pnpm')), false)
  const host = join(moved, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js')
  const result = execFileSync(process.execPath, ['-e', `
    const cordis = require(process.argv[1])
    console.log(JSON.stringify({ cycle: cordis.include().cordis === cordis,
      hostCycle: cordis.include().host() === cordis, outer: cordis.version, nested: cordis.include().version }))
  `, host], { encoding: 'utf8', env: { ...process.env, NODE_PATH: '', NODE_OPTIONS: '' } })
  assert.deepEqual(JSON.parse(result), { cycle: true, hostCycle: true, outer: '1.0.0', nested: '2.0.0' })
})

test('staging supplements required peers from pinned workspace without duplicating shared instances or shipping dev dependencies', t => {
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
  write(deploy, 'node_modules/@deepseek-ai/dsh/package.json', { name: '@deepseek-ai/dsh', version: '0.2.0-rc.2' })
  write(deploy, 'node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture boot')
  const bootManifest = { name: '@deepseek-ai/dsh-app-boot', version: '0.2.0-rc.2', main: 'index.cjs',
    peerDependencies: { '@deepseek-ai/cordis-plugin-group': '*', 'absent-optional-peer': '*' },
    peerDependenciesMeta: { 'absent-optional-peer': { optional: true } } }
  write(deploy, 'node_modules/@deepseek-ai/dsh-app-boot/package.json', bootManifest)
  write(deploy, 'node_modules/@deepseek-ai/dsh-app-boot/index.cjs', `module.exports = require('@deepseek-ai/cordis-plugin-group')`)
  write(deploy, 'node_modules/@deepseek-ai/cordis/package.json', { name: '@deepseek-ai/cordis', version: '1.0.0', main: 'index.cjs' })
  write(deploy, 'node_modules/@deepseek-ai/cordis/index.cjs', `module.exports = { origin: 'exported' }`)
  write(source, 'apps/boot/package.json', bootManifest)
  write(source, 'vendor/group/package.json', { name: '@deepseek-ai/cordis-plugin-group', version: '1.0.0', main: 'index.cjs',
    peerDependencies: { '@deepseek-ai/cordis': '*' }, devDependencies: { 'dev-only': '*' } })
  write(source, 'vendor/group/index.cjs', `module.exports = require('@deepseek-ai/cordis')`)
  write(source, 'vendor/cordis/package.json', { name: '@deepseek-ai/cordis', version: '1.0.0', main: 'index.cjs' })
  write(source, 'vendor/cordis/index.cjs', `module.exports = { origin: 'upstream' }`)
  write(source, 'vendor/group/node_modules/dev-only/package.json', { name: 'dev-only', version: '1.0.0' })
  mkdirSync(join(source, 'apps/boot/node_modules/@deepseek-ai'), { recursive: true })
  directoryLink(join(source, 'vendor/group'), join(source, 'apps/boot/node_modules/@deepseek-ai/cordis-plugin-group'))
  mkdirSync(join(source, 'vendor/group/node_modules/@deepseek-ai'), { recursive: true })
  directoryLink(join(source, 'vendor/cordis'), join(source, 'vendor/group/node_modules/@deepseek-ai/cordis'))
  stage(source, deploy, output, repo)
  assert.equal(existsSync(join(output, 'backend/runtime/node_modules/dev-only')), false)
  assert.equal(existsSync(join(output, 'backend/runtime/node_modules/@deepseek-ai/cordis-plugin-group/node_modules')), false)
  rmSync(source, { recursive: true })
  rmSync(deploy, { recursive: true })
  const moved = join(root, 'isolated extracted peer package')
  cpSync(output, moved, { recursive: true })
  rmSync(output, { recursive: true })
  verifyBundle(moved, readVersions(repo))
  const host = join(moved, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js')
  const result = execFileSync(process.execPath, ['-e', `
    const load = require('node:module').createRequire(process.argv[1])
    const cordis = load('@deepseek-ai/cordis')
    console.log(JSON.stringify({ same: load('@deepseek-ai/dsh-app-boot') === cordis, origin: cordis.origin }))
  `, host],
  { encoding: 'utf8', env: { ...process.env, NODE_PATH: '', NODE_OPTIONS: '' } })
  assert.deepEqual(JSON.parse(result), { same: true, origin: 'exported' })
})

test('staging rejects missing required peers and same-name workspace version drift', t => {
  const root = fixture(t)
  const repo = join(root, 'repo')
  const source = join(root, 'upstream')
  const deploy = join(root, 'deploy')
  project(repo)
  write(source, 'LICENSE', 'upstream license')
  write(deploy, 'package.json', { name: '@deepseek-ai/dsh-desktop-host', version: '0.2.0-rc.2', peerDependencies: { 'missing-peer': '*' } })
  for (const name of ['missing', 'drift']) {
    const output = join(root, name)
    bundle(output)
    if (name === 'drift') {
      write(source, 'apps/host/package.json', { name: '@deepseek-ai/dsh-desktop-host', version: '0.2.0-rc.3' })
      write(source, 'apps/host/node_modules/missing-peer/package.json', { name: 'missing-peer', version: '1.0.0' })
    }
    assert.throws(() => stage(source, deploy, output, repo), /required peer: @deepseek-ai\/dsh-desktop-host -> missing-peer/)
  }
})

test('staging creates a macOS app bundle from flat Native AOT output without runtimeconfig and a direct Linux executable', t => {
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
    bundle(output, rid, true)
    assert.equal(existsSync(join(output, 'DshDesktop.app')), false)
    assert.equal(existsSync(join(output, 'DshDesktop.runtimeconfig.json')), false)
    const dsh = join(root, `${rid}-dsh`)
    write(dsh, 'package.json', { name: '@deepseek-ai/dsh', version: '0.2.0-rc.2' })
    write(dsh, 'lib/profile-boot.js', 'fixture boot')
    mkdirSync(join(deploy, 'node_modules/@deepseek-ai'), { recursive: true })
    const dependency = join(deploy, 'node_modules/@deepseek-ai/dsh')
    directoryLink(dsh, dependency, true)
    assert.equal(readFileSync(join(dependency, 'lib/profile-boot.js'), 'utf8'), 'fixture boot')
    stage(source, deploy, output, repo, rid)
    rmSync(dependency)
    rmSync(dsh, { recursive: true })
    verifyBundle(output, readVersions(repo), rid)
    const packagedDependency = join(resources(output, rid), 'backend/runtime/node_modules/@deepseek-ai/dsh')
    materializedDirectory(packagedDependency)
    materializedFile(join(packagedDependency, 'lib/profile-boot.js'), 'fixture boot')
    assert.equal(existsSync(join(output, 'Run.command')), false)
    assert.equal(existsSync(join(output, 'Run.sh')), false)
    if (rid === 'osx-arm64') {
      materializedFile(executable(output, rid), 'fixture')
      materializedFile(join(resources(output, rid), 'Assets/Backend/launcher.mjs'), 'fixture')
      assert.equal(existsSync(join(output, 'DshDesktop.app/Contents/MacOS/DshDesktop.runtimeconfig.json')), false)
      for (const entry of ['DshDesktop', 'Assets', 'backend']) assert.equal(existsSync(join(output, entry)), false)
      assert.match(readFileSync(join(output, 'DshDesktop.app/Contents/Info.plist'), 'utf8'), /CFBundlePackageType/)
    } else {
      assert.equal(existsSync(join(output, 'DshDesktop')), true)
    }
    assert.equal(JSON.parse(readFileSync(join(resources(output, rid), 'BUILD-INFO.json'))).platform, rid)
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
    if (rid === 'osx-arm64') {
      assert.equal(existsSync(join(output, 'DshDesktop.app/Contents/MacOS/DshDesktop.runtimeconfig.json')), false)
      rmSync(join(output, 'DshDesktop.app/Contents/Info.plist'))
      assert.throws(() => verifyBundle(output, readVersions(root), rid), /missing the macOS app manifest/)
    }
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

test('packaged smoke uses Host pnpm arguments, reports early exit and redacts diagnostics', { skip: !['win32', 'darwin', 'linux'].includes(process.platform) }, async t => {
  const root = fixture(t)
  const rid = process.platform === 'win32' ? 'win-x64' : process.platform === 'darwin' ? 'osx-arm64' : 'linux-x64'
  bundle(root, rid)
  const rootResources = resources(root, rid)
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.2.0-rc.2' })
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture')
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/package.json', { version: '0.2.0-rc.2', type: 'module' })
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    import { existsSync } from 'node:fs'
    if (!process.argv[5]?.endsWith('pnpm.mjs') || !existsSync(process.argv[5]) || !process.argv[6]) {
      throw new Error('Host did not receive pnpm and Node bin paths')
    }
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
  await assert.rejects(() => smoke(root, rid), /fatal: fixture startup failure/)
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    import '@deepseek-ai/missing-runtime-peer'
  `)
  const started = Date.now()
  await assert.rejects(() => smoke(root, rid, { timeoutMs: 5000 }), error => {
    assert.match(error.message, /Host exited before readiness/)
    assert.match(error.message, /missing-runtime-peer/)
    assert.doesNotMatch(error.message, /timed out/)
    return true
  })
  assert.ok(Date.now() - started < 5000, 'Import failure must fail before the readiness timeout')
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    process.stderr.write('token="stderr-secret" https://localhost/?token=url-secret\\n')
    process.send({ type: 'fatal', message: 'api_key="fatal-secret" Bearer bearer-secret', stderrTail: 'password="tail-secret"' })
  `)
  await assert.rejects(() => smoke(root, rid), error => {
    for (const secret of ['stderr-secret', 'url-secret', 'fatal-secret', 'bearer-secret', 'tail-secret']) {
      assert.ok(!error.message.includes(secret), 'Failure diagnostics leaked a secret')
    }
    assert.match(error.message, /redacted/)
    return true
  })
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    process.on('message', () => {})
  `)
  await assert.rejects(() => smoke(root, rid, { timeoutMs: 300 }), /startup \(waiting for ready\): timed out/)
  write(rootResources, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    process.send({ type: 'ready', url: 'http://127.0.0.1:19387/?token=timeout-secret' })
    process.on('message', () => {})
  `)
  await assert.rejects(() => smoke(root, rid, { timeoutMs: 1000 }), /shutdown \(waiting for shutdown-complete\): timed out/)
})
