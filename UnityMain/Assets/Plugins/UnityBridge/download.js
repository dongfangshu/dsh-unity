// Download Roslyn 3.8.0 + EXACT runtime deps from nuget.org (flat container)
// via Node fetch, extract each nupkg, and place the DLLs flat in Editor/ —
// the standard UPM location for bundled editor plugins (scoped to the
// DSH.UnityBridge.Editor asmdef via its precompiledReferences list).
//
// Why Node: schannel (pwsh/curl) cannot establish TLS on this machine; Node's
// OpenSSL works. Extraction shells out to PowerShell Expand-Archive.
//
// Assembly versions must match what Microsoft.CodeAnalysis 3.8.0 references:
// Immutable 5.0.0.0, Ref.Metadata 5.0.0.0, Memory 4.0.1.1, Tasks.Extensions
// 4.2.0.1, Unsafe 4.0.6.0, CodePages 4.1.1.0, Buffers 4.0.3.0, Numerics.Vectors 4.1.4.0.
//
// Usage:  node download.js   (run from the package folder or anywhere)
const fs = require('fs')
const path = require('path')
const { spawnSync } = require('child_process')

const DL = path.join(__dirname, '..', '..', '..', '..', '.roslyn-download')
const EDITOR = path.join(__dirname, 'Editor')

const pkgs = [
  { id: 'microsoft.codeanalysis.csharp.scripting', ver: '3.8.0', dll: 'Microsoft.CodeAnalysis.CSharp.Scripting.dll' },
  { id: 'microsoft.codeanalysis.csharp', ver: '3.8.0', dll: 'Microsoft.CodeAnalysis.CSharp.dll' },
  { id: 'microsoft.codeanalysis.common', ver: '3.8.0', dll: 'Microsoft.CodeAnalysis.dll' },
  { id: 'microsoft.codeanalysis.scripting.common', ver: '3.8.0', dll: 'Microsoft.CodeAnalysis.Scripting.dll' },
  { id: 'system.text.encoding.codepages', ver: '4.5.1', dll: 'System.Text.Encoding.CodePages.dll' },
  { id: 'system.collections.immutable', ver: '5.0.0', dll: 'System.Collections.Immutable.dll' },
  { id: 'system.reflection.metadata', ver: '5.0.0', dll: 'System.Reflection.Metadata.dll' },
  { id: 'system.memory', ver: '4.5.4', dll: 'System.Memory.dll' },
  { id: 'system.buffers', ver: '4.5.1', dll: 'System.Buffers.dll' },
  { id: 'system.numerics.vectors', ver: '4.5.0', dll: 'System.Numerics.Vectors.dll' },
  { id: 'system.runtime.compilerservices.unsafe', ver: '4.7.1', dll: 'System.Runtime.CompilerServices.Unsafe.dll' },
  { id: 'system.threading.tasks.extensions', ver: '4.5.4', dll: 'System.Threading.Tasks.Extensions.dll' },
]

fs.mkdirSync(DL, { recursive: true })
fs.mkdirSync(EDITOR, { recursive: true })

async function download(p) {
  const url = `https://api.nuget.org/v3-flatcontainer/${p.id}/${p.ver}/${p.id}.${p.ver}.nupkg`
  const res = await fetch(url)
  if (!res.ok) throw new Error(`${p.id}: HTTP ${res.status}`)
  const buf = Buffer.from(await res.arrayBuffer())
  const file = path.join(DL, `${p.id}.${p.ver}.nupkg`)
  fs.writeFileSync(file, buf)
  return file
}

function extractAndPlace(p, nupkg) {
  const dir = path.join(DL, `${p.id}.${p.ver}`)
  // stdio 'ignore': the sandbox forbids capturing child stdout through pipes
  const r = spawnSync('powershell', [
    '-NoProfile', '-Command',
    `Expand-Archive -Path '${nupkg}' -DestinationPath '${dir}' -Force`,
  ], { stdio: 'ignore' })
  if (r.status !== 0) throw new Error(`${p.id}: expand failed (status ${r.status})`)
  const src = path.join(dir, 'lib', 'netstandard2.0', p.dll)
  if (!fs.existsSync(src)) throw new Error(`${p.id}: ${p.dll} not found under lib/netstandard2.0`)
  fs.copyFileSync(src, path.join(EDITOR, p.dll))
  console.log(`OK ${p.id} ${p.ver} -> Editor/${p.dll}`)
}

;(async () => {
  const results = await Promise.allSettled(pkgs.map(async (p) => {
    const nupkg = await download(p)
    extractAndPlace(p, nupkg)
  }))
  const failed = results.filter((r) => r.status === 'rejected')
  for (const f of failed) console.error('FAIL', f.reason.message)
  if (failed.length) process.exit(1)
  console.log(`all ${pkgs.length} DLLs in place under Editor/`)
})()
