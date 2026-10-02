import assert from 'node:assert/strict'
import { cpSync, mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
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

function bundle(root) {
  for (const path of [
    'DshDesktop.exe', 'Assets/Backend/launcher.mjs',
    'backend/primary-runtime/dependencies/node/bin/node.exe',
    'backend/primary-runtime/dependencies/pnpm/bin/pnpm.mjs',
    'backend/office-skills/scripts/check_office.py',
  ]) write(root, path, 'fixture')
  write(root, 'backend/primary-runtime/runtime.json', { desktopVersion: '0.2.0-rc.2', node: '24.21.0', platform: 'win32', arch: 'x64' })
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

test('staging materializes linked dependencies and records real backend start settings', t => {
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
  assert.match(readFileSync(join(output, 'Run.cmd'), 'utf8'), /DSH_DESKTOP_BACKEND_MODE=real/)
  assert.match(readFileSync(join(output, 'Run.cmd'), 'utf8'), /%~dp0backend/)
  assert.equal(JSON.parse(readFileSync(join(output, 'BUILD-INFO.json'))).clientVersion, '0.1.0')
  assert.throws(() => stage(source, deploy, output, repo), /must be new/)
})

test('bundle validation rejects version drift and missing launcher', t => {
  const root = fixture(t)
  project(root)
  bundle(root)
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.1.7-rc.2' })
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/package.json', { version: '0.2.0-rc.2' })
  assert.throws(() => verifyBundle(root, readVersions(root)), /differs from pin/)
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.2.0-rc.2' })
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture')
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', 'fixture')
  rmSync(join(root, 'Assets/Backend/launcher.mjs'))
  assert.throws(() => verifyBundle(root, readVersions(root)), /missing Assets/)
})

test('packaged smoke observes readiness and clean shutdown with bundled Node', { skip: process.platform !== 'win32' }, async t => {
  const root = fixture(t)
  bundle(root)
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/package.json', { version: '0.2.0-rc.2' })
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js', 'fixture')
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/package.json', { version: '0.2.0-rc.2', type: 'module' })
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    process.send({ type: 'ready', url: 'http://127.0.0.1:19387/?token=fixture' })
    process.on('message', message => {
      if (message.type === 'shutdown') {
        process.send({ type: 'shutdown-complete' }, () => process.disconnect())
      }
    })
  `)
  cpSync(process.execPath, join(root, 'backend/primary-runtime/dependencies/node/bin/node.exe'))
  cpSync('DshDesktop.Infrastructure/Assets/Backend/launcher.mjs', join(root, 'Assets/Backend/launcher.mjs'))
  await smoke(root)
  write(root, 'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', `
    process.send({ type: 'fatal', message: 'fixture startup failure' }, () => process.exit(1))
  `)
  await assert.rejects(() => smoke(root), /Invalid launcher control frame/)
})
