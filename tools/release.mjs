import { spawn, spawnSync, execFileSync } from 'node:child_process'
import { appendFileSync, cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
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

export function verifyBundle(bundle, versions = readVersions()) {
  const runtime = join(bundle, 'backend', 'runtime')
  const dsh = readJson(join(runtime, 'node_modules/@deepseek-ai/dsh/package.json'))
  const host = readJson(join(runtime, 'node_modules/@deepseek-ai/dsh-desktop-host/package.json'))
  if (dsh.version !== versions.version || host.version !== versions.version) throw new Error('Bundled DSH/Host version differs from pin')
  const manifest = readJson(join(bundle, 'backend', 'primary-runtime', 'runtime.json'))
  for (const file of [
    'DshDesktop.exe', 'Assets/Backend/launcher.mjs',
    'backend/runtime/node_modules/@deepseek-ai/dsh/lib/profile-boot.js',
    'backend/runtime/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js',
    'backend/primary-runtime/dependencies/node/bin/node.exe',
    'backend/primary-runtime/dependencies/pnpm/bin/pnpm.mjs',
    'backend/office-skills/scripts/check_office.py',
  ]) {
    if (!existsSync(join(bundle, file))) throw new Error(`Bundle is missing ${file}`)
  }
  if (manifest.desktopVersion !== versions.version || manifest.platform !== 'win32' || manifest.arch !== 'x64') {
    throw new Error('Primary runtime version/platform mismatch')
  }
  verifyTree(bundle)
}

export function stage(source, deployed, bundle, root = repository) {
  const versions = readVersions(root)
  if (readJson(join(deployed, 'package.json')).name !== '@deepseek-ai/dsh-desktop-host') throw new Error('Expected a deployed desktop-host package')
  const runtime = join(bundle, 'backend', 'runtime')
  if (existsSync(runtime)) throw new Error('Runtime staging directory must be new')
  mkdirSync(runtime, { recursive: true })
  // Resolve pnpm links into ordinary files before archiving, including private Host.
  cpSync(join(deployed, 'node_modules'), join(runtime, 'node_modules'), { recursive: true, dereference: true })
  const host = join(runtime, 'node_modules', '@deepseek-ai', 'dsh-desktop-host')
  mkdirSync(host, { recursive: true })
  cpSync(join(deployed, 'lib'), join(host, 'lib'), { recursive: true, dereference: true })
  cpSync(join(deployed, 'package.json'), join(host, 'package.json'))
  writeFileSync(join(runtime, 'package.json'), JSON.stringify({ name: 'dsh-desktop-runtime', private: true, version: versions.version, type: 'module' }, null, 2) + '\n')
  cpSync(join(root, 'LICENSE'), join(bundle, 'LICENSE'))
  cpSync(join(root, 'NOTICE'), join(bundle, 'NOTICE'))
  cpSync(join(source, 'LICENSE'), join(bundle, 'DSH-LICENSE'))
  // Run.cmd scopes all settings to this process and uses paths relative to the ZIP.
  writeFileSync(join(bundle, 'Run.cmd'), [
    '@echo off', 'setlocal',
    'set "DSH_DESKTOP_BACKEND_MODE=real"',
    'set "DSH_DESKTOP_RUNTIME_DIR=%~dp0backend\\runtime"',
    'set "DSH_DESKTOP_NODE=%~dp0backend\\primary-runtime\\dependencies\\node\\bin\\node.exe"',
    'set "DSH_DESKTOP_PRIMARY_RUNTIME=%~dp0backend\\primary-runtime"',
    'set "DSH_DESKTOP_LAUNCHER=%~dp0Assets\\Backend\\launcher.mjs"',
    'set "DSH_DESKTOP_RESOLUTION=runtime"',
    'set "PATH=%~dp0backend\\primary-runtime\\dependencies\\node\\bin;%PATH%"',
    '"%~dp0DshDesktop.exe"', 'exit /b %errorlevel%', '',
  ].join('\n'))
  writeFileSync(join(bundle, 'BUILD-INFO.json'), JSON.stringify({
    clientVersion: versions.appVersion, dshVersion: versions.version, dshRef: versions.ref,
    dshCommit: versions.commit, nodeVersion: readJson(join(bundle, 'backend/primary-runtime/runtime.json')).node,
    platform: 'win-x64',
  }, null, 2) + '\n')
  writeFileSync(join(bundle, 'START-HERE.txt'), 'Extract the entire ZIP to a writable folder, then launch Run.cmd.\nLaunching DshDesktop.exe directly uses the normal development environment settings.\nNode, Python, pnpm and Office skill assets are bundled; no .NET installation is required.\nDSH_HOME or ~/.dsh stores sessions and credentials. This package shares that home.\nThis is an unsigned portable Windows x64 package, not an installer.\n')
  verifyBundle(bundle, versions)
  console.log('Portable runtime staged and package versions verified')
}

export async function smoke(bundle) {
  verifyBundle(bundle)
  const scratch = mkdtempSync(join(tmpdir(), 'dsh-release-smoke-'))
  const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(?:DSH_|NODE_OPTIONS|NODE_PATH)|(?:KEY|SECRET|TOKEN|PASSWORD)/iu.test(name)))
  const child = spawn(join(bundle, 'backend/primary-runtime/dependencies/node/bin/node.exe'), [
    join(bundle, 'Assets/Backend/launcher.mjs'),
    '--runtime-dir', join(bundle, 'backend/runtime'), '--profile-dir', join(scratch, 'profile'),
    '--primary-runtime', join(bundle, 'backend/primary-runtime'), '--resolution', 'runtime', '--dsh-home', join(scratch, 'home'),
  ], { env: environment, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
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
      // Terminate the launcher's complete process tree, including the Host.
      spawnSync('taskkill.exe', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true, timeout: 15_000 })
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
  else if (command === 'stage' && args.length === 3) stage(...args.map(path => resolve(path)))
  else if (command === 'smoke' && args.length === 1) await smoke(resolve(args[0]))
  else throw new Error('Usage: release.mjs metadata | source <checkout> | stage <checkout> <deploy> <bundle> | smoke <bundle>')
}
