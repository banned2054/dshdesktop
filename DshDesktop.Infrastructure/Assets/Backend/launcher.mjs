#!/usr/bin/env node
/**
 * DshDesktop backend launcher (control protocol v1).
 *
 * Bridges the Node IPC channel of @deepseek-ai/dsh-desktop-host to a
 * line-delimited JSON control protocol so a non-Node parent can supervise
 * the Host process:
 *
 *   stdout - one JSON object per line, control messages only (never logs)
 *   stderr - launcher and Host logs
 *   stdin  - one JSON object per line, currently only { "type": "shutdown" }
 *
 * Every stdout message carries "v": 1 (control protocol version).
 *
 * Usage:
 *   node launcher.mjs --runtime-dir <dir> --profile-dir <dir>
 *                     --primary-runtime <dir> [--resolution link|runtime]
 *                     --dsh-home <dir>
 */

import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import { copyFileSync, existsSync, linkSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

const CONTROL_PROTOCOL_VERSION = 1
const HOST_GRACEFUL_SHUTDOWN_MS = 10_000
const HOST_SIGTERM_GRACE_MS = 5_000
const HOST_SIGKILL_GRACE_MS = 5_000
const MAX_HOST_STDERR_CHARS = 64 * 1024

function parseArguments(argv) {
  const values = {}
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index]
    if (!name.startsWith('--')) throw new Error(`launcher: unexpected argument ${JSON.stringify(name)}`)
    const key = name.slice(2)
    const value = argv[index + 1]
    if (value === undefined || value.startsWith('--')) throw new Error(`launcher: missing value for --${key}`)
    values[key] = value
  }
  for (const required of ['runtime-dir', 'profile-dir', 'primary-runtime', 'dsh-home']) {
    if (values[required] === undefined) throw new Error(`launcher: --${required} is required`)
  }
  if (values.resolution !== undefined && values.resolution !== 'link' && values.resolution !== 'runtime') {
    throw new Error(`launcher: --resolution must be link or runtime, got ${JSON.stringify(values.resolution)}`)
  }
  return values
}

function emit(message) {
  process.stdout.write(`${JSON.stringify({ v: CONTROL_PROTOCOL_VERSION, ...message })}\n`)
}

function log(text) {
  process.stderr.write(`[launcher] ${text}\n`)
}

/** Initialize the desktop plugin profile the same way the Electron app does. */
function ensureProfileDir(profileDir) {
  mkdirSync(profileDir, { recursive: true })
  const manifestPath = join(profileDir, 'package.json')
  if (!existsSync(manifestPath)) {
    const manifest = {
      name: 'dsh-profile-desktop',
      private: true,
      dependencies: {},
      dsh: { profile: { bundles: ['@deepseek-ai/dsh-base', '@deepseek-ai/dsh-web-app'] } },
    }
    writeFileSync(manifestPath, `${JSON.stringify(manifest, undefined, 2)}\n`)
  }
  const workspacePath = join(profileDir, 'pnpm-workspace.yaml')
  if (!existsSync(workspacePath)) {
    writeFileSync(workspacePath, 'packages:\n  - .\n\nnodeLinker: hoisted\nautoInstallPeers: false\n')
  }
  // OS-assigned webserver port: avoids the fixed-port conflicts of the packaged
  // desktop app; the real port is reported in the ready message URL.
  const patchPath = join(profileDir, 'cordis.patch.yml')
  if (!existsSync(patchPath)) {
    writeFileSync(patchPath, '- id: webserver\n  config:\n    host: 127.0.0.1\n    port: 0\n')
  }
}

/**
 * The office skills plugin fails Host startup when its asset root is missing.
 * Development layouts have no bundled payload, so provide the minimal asset set
 * the plugin probes at activation; real payloads replace the whole directory.
 */
function ensurePrimaryRuntimeStub(primaryRuntimeDir) {
  mkdirSync(primaryRuntimeDir, { recursive: true })
  const assetRoot = join(dirname(primaryRuntimeDir), 'office-skills')
  const probe = join(assetRoot, 'scripts', 'check_office.py')
  if (!existsSync(probe)) {
    mkdirSync(dirname(probe), { recursive: true })
    writeFileSync(probe, '# DshDesktop development stub\n')
  }
  for (const skillName of ['office-docx', 'office-pptx', 'office-xlsx']) {
    const skillPath = join(assetRoot, skillName, 'SKILL.md')
    if (existsSync(skillPath)) continue
    mkdirSync(dirname(skillPath), { recursive: true })
    writeFileSync(skillPath,
      `---\nname: ${skillName}\ndescription: Development stub; the real payload is not installed.\n---\n\nDevelopment stub skill.\n`)
  }
  // The desktop host passes the packaged Node location (dependencies/node/bin)
  // to the office plugin, which stats it at activation. Link the running
  // executable; a hard link keeps the copy honest across Node upgrades in place.
  const nodePath = join(primaryRuntimeDir, 'dependencies', 'node', 'bin',
                        process.platform === 'win32' ? 'node.exe' : 'node')
  if (!existsSync(nodePath)) {
    mkdirSync(dirname(nodePath), { recursive: true })
    try {
      linkSync(process.execPath, nodePath)
    } catch {
      // Cross-volume links (EXDEV) and restricted filesystems fall back to a copy.
      copyFileSync(process.execPath, nodePath)
    }
  }
}

function exitsWithin(exit, milliseconds) {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(false), milliseconds)
    exit.then(() => { clearTimeout(timer); resolve(true) }, () => { clearTimeout(timer); resolve(true) })
  })
}

async function main() {
  const args = parseArguments(process.argv.slice(2))
  ensureProfileDir(args['profile-dir'])
  ensurePrimaryRuntimeStub(args['primary-runtime'])

  const entry = join(args['runtime-dir'], 'node_modules', '@deepseek-ai', 'dsh-desktop-host', 'lib', 'index.js')
  if (!existsSync(entry)) throw new Error(`launcher: desktop host entry not found: ${entry}`)

  const child = spawn(process.execPath, [
    '--expose-internals',
    entry,
    args['runtime-dir'],
    args['profile-dir'],
    args['primary-runtime'],
    args.resolution ?? 'link',
  ], {
    cwd: args['profile-dir'],
    env: { ...process.env, DSH_HOME: args['dsh-home'] },
    stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
  })
  log(`started host pid ${String(child.pid)}: ${entry}`)

  let stderrTail = ''
  const appendStderr = (chunk) => {
    stderrTail = (stderrTail + chunk).slice(-MAX_HOST_STDERR_CHARS)
    process.stderr.write(chunk)
  }
  child.stdout.setEncoding('utf8')
  child.stdout.on('data', (chunk) => process.stderr.write(chunk))
  child.stderr.setEncoding('utf8')
  child.stderr.on('data', appendStderr)

  const exitPromise = new Promise((resolve) => { child.once('close', resolve) })
  let stopping = false
  let shutdownCompleted = false

  child.on('message', (message) => {
    if (typeof message !== 'object' || message === null || typeof message.type !== 'string') return
    if (message.type === 'ready' && typeof message.url === 'string') {
      emit({ type: 'ready', url: message.url, pid: child.pid })
    } else if (message.type === 'fatal' && typeof message.message === 'string') {
      emit({ type: 'fatal', message: message.message, stderrTail: stderrTail.trim() })
    } else if (message.type === 'shutdown-complete') {
      shutdownCompleted = true
      emit({ type: 'shutdown-complete' })
    }
    // update-tasks control is desktop-update specific and not forwarded in v1.
  })

  child.once('error', (error) => {
    emit({ type: 'fatal', message: `failed to start host process: ${error.message}`, stderrTail: stderrTail.trim() })
  })

  exitPromise.then((code) => {
    emit({ type: 'exited', code: child.exitCode, signal: child.signalCode ?? null, clean: stopping && shutdownCompleted })
    process.exitCode = child.exitCode ?? 1
  })

  const stop = async () => {
    if (stopping) return
    stopping = true
    emit({ type: 'stopping' })
    if (child.connected) child.send({ type: 'shutdown' })
    if (!await exitsWithin(exitPromise, HOST_GRACEFUL_SHUTDOWN_MS)) child.kill('SIGTERM')
    if (!await exitsWithin(exitPromise, HOST_SIGTERM_GRACE_MS)) child.kill('SIGKILL')
    if (!await exitsWithin(exitPromise, HOST_SIGKILL_GRACE_MS)) {
      emit({ type: 'error', message: `host pid ${String(child.pid)} did not exit after SIGKILL` })
      process.exitCode = 1
    }
  }

  const stdin = createInterface({ input: process.stdin })
  stdin.on('line', (line) => {
    if (line.trim() === '') return
    let message
    try { message = JSON.parse(line) } catch { log(`ignored malformed control line: ${line}`); return }
    if (typeof message !== 'object' || message === null) return
    if (message.type === 'shutdown') void stop()
  })
  // Parent closed the control pipe: tear the Host down instead of leaking it.
  process.stdin.on('close', () => { void stop() })
  process.stdin.on('end', () => { void stop() })
  for (const signal of ['SIGINT', 'SIGTERM']) {
    process.on(signal, () => { void stop() })
  }
}

main().catch((error) => {
  const message = error instanceof Error ? error.message : String(error)
  emit({ type: 'error', message })
  process.exitCode = 1
})
