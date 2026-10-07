import { spawn, spawnSync, execFileSync } from 'node:child_process'
import { appendFileSync, chmodSync, cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { createInterface } from 'node:readline'
import { fileURLToPath } from 'node:url'

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const readJson = path => JSON.parse(readFileSync(path, 'utf8'))
const semver = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$/u

export function readVersions(root = repository, tag = '') {
  const backend = readJson(join(root, 'backend-version.json'))
  const project = readFileSync(join(root, 'DshDesktop', 'DshDesktop.csproj'), 'utf8')
  const version = project.match(/<Version>\s*([^<]+?)\s*<\/Version>/u)?.[1]
  if (!semver.test(version ?? '') || !semver.test(backend.version ?? '')) throw new Error('Invalid client or DSH version')
  if (!/^[a-f0-9]{40}$/u.test(backend.commit ?? '')) throw new Error('DSH commit must be a full SHA')
  if (backend.ref !== `dsh-v${backend.version}`) throw new Error('DSH ref must match its version')
  if (tag && tag !== `v${version}`) throw new Error(`Release tag must be v${version}; found ${tag}`)
  return { ...backend, appVersion: version }
}

function emitOutputs(values) {
  if (process.env.GITHUB_OUTPUT) {
    appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(values).map(([name, value]) => `${name}=${value}\n`).join(''))
  }
  console.log(JSON.stringify(values))
}

export function verifySource(source, root = repository) {
  const versions = readVersions(root)
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: source, encoding: 'utf8' }).trim()
  const tagged = execFileSync('git', ['rev-parse', `${versions.ref}^{commit}`], { cwd: source, encoding: 'utf8' }).trim()
  if (head !== versions.commit || tagged !== versions.commit) throw new Error('DSH checkout/tag differs from pinned commit')
  for (const file of ['package.json', 'apps/cli/package.json', 'apps/desktop-host/package.json']) {
    if (readJson(join(source, file)).version !== versions.version) throw new Error(`DSH version mismatch: ${file}`)
  }
  const node = readJson(join(source, 'scripts/primary-runtime/lock.json')).nodeVersion
  const manager = readJson(join(source, 'package.json')).packageManager
  const pnpm = /^pnpm@(\d+\.\d+\.\d+)$/u.exec(manager)?.[1]
  if (!pnpm || !/^\d+\.\d+\.\d+$/u.test(node ?? '')) throw new Error('Upstream toolchain must have exact versions')
  return { node, pnpm }
}

export function verifyTree(root) {
  for (const name of readdirSync(root)) {
    const path = join(root, name)
    const stat = lstatSync(path)
    if (stat.isSymbolicLink()) throw new Error(`Release contains a filesystem link: ${path}`)
    if (stat.isDirectory()) verifyTree(path)
  }
}

const targets = {
  'win-x64': { platform: 'win32', arch: 'x64', executable: 'DshDesktop.exe', node: 'node.exe' },
  'osx-arm64': { platform: 'darwin', arch: 'arm64', executable: 'DshDesktop', node: 'node' },
  'linux-x64': { platform: 'linux', arch: 'x64', executable: 'DshDesktop', node: 'node' },
}

function resourceRoot(bundle, rid) {
  return rid === 'osx-arm64' ? join(bundle, 'DshDesktop.app', 'Contents', 'Resources') : bundle
}

function executablePath(bundle, rid) {
  const target = targetFor(rid)
  return rid === 'osx-arm64'
    ? join(bundle, 'DshDesktop.app', 'Contents', 'MacOS', target.executable)
    : join(bundle, target.executable)
}

function targetFor(rid) {
  const target = targets[rid]
  if (!target) throw new Error(`Unsupported release target: ${rid}`)
  return target
}

function createMacApplicationBundle(bundle, target, versions) {
  const app = join(bundle, 'DshDesktop.app')
  const contents = join(app, 'Contents')
  const macOS = join(contents, 'MacOS')
  const resources = join(contents, 'Resources')
  mkdirSync(macOS, { recursive: true })
  mkdirSync(resources, { recursive: true })
  for (const entry of readdirSync(bundle)) {
    if (entry === 'DshDesktop.app') continue
    const source = join(bundle, entry)
    const destination = entry === 'backend' || entry === 'Assets' ||
      ['LICENSE', 'NOTICE', 'DSH-LICENSE', 'BUILD-INFO.json', 'START-HERE.txt'].includes(entry)
      ? join(resources, entry)
      : join(macOS, entry)
    renameSync(source, destination)
  }
  writeFileSync(join(contents, 'Info.plist'), `<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n<plist version="1.0"><dict><key>CFBundleExecutable</key><string>${target.executable}</string><key>CFBundleIdentifier</key><string>com.dshdesktop.app</string><key>CFBundleName</key><string>DSH Desktop</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleShortVersionString</key><string>${versions.appVersion}</string><key>CFBundleVersion</key><string>${versions.appVersion}</string></dict></plist>\n`)
  chmodSync(join(macOS, target.executable), 0o755)
}

export function verifyBundle(bundle, versions = readVersions(), rid = 'win-x64') {
  const target = targetFor(rid)
  const resources = resourceRoot(bundle, rid)
  const runtime = join(resources, 'backend', 'runtime')
  if (!existsSync(executablePath(bundle, rid))) throw new Error(`Bundle is missing ${target.executable}`)
  const dsh = readJson(join(runtime, 'node_modules/@deepseek-ai/dsh/package.json'))
  const host = readJson(join(runtime, 'node_modules/@deepseek-ai/dsh-desktop-host/package.json'))
  if (dsh.version !== versions.version || host.version !== versions.version) throw new Error('Bundled DSH/Host version differs from pin')
  const manifest = readJson(join(resources, 'backend', 'primary-runtime', 'runtime.json'))
  for (const file of [
    'Assets/Backend/launcher.mjs',
    'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js',
    'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js',
    `backend/primary-runtime/dependencies/node/bin/${target.node}`,
    'backend/primary-runtime/dependencies/pnpm/bin/pnpm.mjs',
    'backend/office-skills/scripts/check_office.py',
  ]) {
    if (!existsSync(join(resources, file))) throw new Error(`Bundle is missing ${file}`)
  }
  if (rid === 'osx-arm64' && !existsSync(join(bundle, 'DshDesktop.app', 'Contents', 'MacOS', 'DshDesktop.runtimeconfig.json')))
    throw new Error('Bundle is missing the macOS runtime configuration')
  if (rid === 'osx-arm64' && !existsSync(join(bundle, 'DshDesktop.app', 'Contents', 'Info.plist')))
    throw new Error('Bundle is missing the macOS app manifest')
  if (manifest.desktopVersion !== versions.version || manifest.platform !== target.platform || manifest.arch !== target.arch) {
    throw new Error('Primary runtime version/platform mismatch')
  }
  verifyTree(bundle)
}

export function stage(source, deployed, bundle, root = repository, rid = 'win-x64') {
  const target = targetFor(rid)
  const versions = readVersions(root)
  if (readJson(join(deployed, 'package.json')).name !== '@deepseek-ai/dsh-desktop-host') throw new Error('Expected a deployed desktop-host package')
  if (rid === 'osx-arm64') createMacApplicationBundle(bundle, target, versions)
  const resources = resourceRoot(bundle, rid)
  const runtime = join(resources, 'backend', 'runtime')
  if (existsSync(runtime)) throw new Error('Runtime staging directory must be new')
  mkdirSync(runtime, { recursive: true })
  // Resolve pnpm links into ordinary files before archiving, including private Host.
  cpSync(join(deployed, 'node_modules'), join(runtime, 'node_modules'), { recursive: true, dereference: true })
  const host = join(runtime, 'node_modules', '@deepseek-ai', 'dsh-desktop-host')
  mkdirSync(host, { recursive: true })
  cpSync(join(deployed, 'lib'), join(host, 'lib'), { recursive: true, dereference: true })
  cpSync(join(deployed, 'package.json'), join(host, 'package.json'))
  writeFileSync(join(runtime, 'package.json'), JSON.stringify({ name: 'dsh-desktop-runtime', private: true, version: versions.version, type: 'module' }, null, 2) + '\n')
  cpSync(join(root, 'LICENSE'), join(resources, 'LICENSE'))
  cpSync(join(root, 'NOTICE'), join(resources, 'NOTICE'))
  cpSync(join(source, 'LICENSE'), join(resources, 'DSH-LICENSE'))
  if (rid !== 'win-x64') {
    chmodSync(executablePath(bundle, rid), 0o755)
    chmodSync(join(resources, `backend/primary-runtime/dependencies/node/bin/${target.node}`), 0o755)
  }
  writeFileSync(join(resources, 'BUILD-INFO.json'), JSON.stringify({
    clientVersion: versions.appVersion, dshVersion: versions.version, dshRef: versions.ref,
    dshCommit: versions.commit, nodeVersion: readJson(join(resources, 'backend/primary-runtime/runtime.json')).node,
    platform: rid,
  }, null, 2) + '\n')
  const platformDescription = { 'win-x64': 'Windows x64', 'osx-arm64': 'macOS arm64 (Apple Silicon)', 'linux-x64': 'Linux x64' }[rid]
  const entryPoint = rid === 'osx-arm64' ? 'DshDesktop.app' : target.executable
  writeFileSync(join(resources, 'START-HERE.txt'), `Extract the entire ZIP to a writable folder, then open ${entryPoint}.\nThe package includes the desktop client, Node, Python, pnpm and Office skill assets; no .NET installation is required.\nDSH_HOME or ~/.dsh stores sessions and credentials. This package shares that home.\nThis is an unsigned portable ${platformDescription} package, not an installer.\n`)
  verifyBundle(bundle, versions, rid)
  console.log(`${platformDescription} runtime staged and package versions verified`)
}

export async function smoke(bundle, rid = 'win-x64') {
  const target = targetFor(rid)
  const resources = resourceRoot(bundle, rid)
  verifyBundle(bundle, readVersions(), rid)
  const scratch = mkdtempSync(join(tmpdir(), 'dsh-release-smoke-'))
  const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(?:DSH_|NODE_OPTIONS|NODE_PATH)|(?:KEY|SECRET|TOKEN|PASSWORD)/iu.test(name)))
  const child = spawn(join(resources, 'backend/primary-runtime/dependencies/node/bin', target.node), [
    join(resources, 'Assets/Backend/launcher.mjs'),
    '--runtime-dir', join(resources, 'backend/runtime'), '--profile-dir', join(scratch, 'profile'),
    '--primary-runtime', join(resources, 'backend/primary-runtime'), '--resolution', 'runtime', '--dsh-home', join(scratch, 'home'),
  ], { env: environment, stdio: ['pipe', 'pipe', 'pipe'], detached: process.platform !== 'win32' })
  const closed = new Promise(resolvePromise => child.once('close', resolvePromise))
  try {
    await new Promise((resolvePromise, reject) => {
      let ready = false
      let stopped = false
      // Discard backend output: ready URLs contain authentication tokens.
      child.stderr.resume()
      const timer = setTimeout(() => reject(new Error('Runtime readiness/shutdown timed out')), 120_000)
      child.once('error', error => { clearTimeout(timer); reject(error) })
      child.stdin.on('error', error => { clearTimeout(timer); reject(error) })
      child.once('close', code => {
        clearTimeout(timer)
        if (code === 0 && ready && stopped) resolvePromise()
        else reject(new Error(`Runtime failed readiness/clean shutdown (exit ${code})`))
      })
      createInterface({ input: child.stdout }).on('line', line => {
        try {
          const message = JSON.parse(line)
          if (message.v !== 1) throw new Error('Unexpected launcher protocol')
          if (message.type === 'fatal') throw new Error('Runtime reported a fatal startup error')
          if (message.type === 'ready' && !ready) {
            ready = true
            // Validate the ready endpoint without exposing its credentials.
            const url = new URL(message.url)
            if (url.hostname !== '127.0.0.1' && url.hostname !== 'localhost') throw new Error('Unexpected runtime host')
            child.stdin.end('{"type":"shutdown"}\n')
            timer.refresh()
          }
          if (message.type === 'shutdown-complete') stopped = true
        } catch { clearTimeout(timer); reject(new Error('Invalid launcher control frame')) }
      })
    })
    console.log('Extracted backend: ready + shutdown-complete + exit 0')
  } finally {
    // Even a failed startup must release the launcher's stdin reader.
    if (!child.stdin.writableEnded) child.stdin.end()
    if (child.exitCode === null && child.pid) {
      if (process.platform === 'win32') {
        spawnSync('taskkill.exe', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore', timeout: 15_000 })
      } else {
        try { process.kill(-child.pid, 'SIGTERM') } catch {}
      }
      let cleanupTimer
      try {
        await Promise.race([closed, new Promise((_, reject) => {
          cleanupTimer = setTimeout(() => reject(new Error('Could not terminate the smoke process tree')), 15_000)
        })])
      } finally { clearTimeout(cleanupTimer) }
    }
    rmSync(scratch, { recursive: true, force: true })
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, ...args] = process.argv.slice(2)
  if (command === 'metadata') {
    const { appVersion, commit } = readVersions(repository, process.env.RELEASE_TAG)
    emitOutputs({ version: appVersion, commit })
  } else if (command === 'source' && args.length === 1) emitOutputs(verifySource(resolve(args[0])))
  else if (command === 'stage' && args.length === 4) stage(...args.slice(0, 3).map(path => resolve(path)), repository, args[3])
  else if (command === 'smoke' && (args.length === 1 || args.length === 2)) await smoke(resolve(args[0]), args[1] ?? 'win-x64')
  else throw new Error('Usage: release.mjs metadata | source <checkout> | stage <checkout> <deploy> <bundle> <rid> | smoke <bundle> [rid]')
}
