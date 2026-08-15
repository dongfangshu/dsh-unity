// Download Roslyn 3.8.0 + EXACT runtime deps from nuget.org (flat container) via Node fetch.
// schannel (pwsh/curl) cannot establish TLS on this machine; Node's OpenSSL works.
// Assembly versions must match what Microsoft.CodeAnalysis 3.8.0 references:
// Immutable 5.0.0.0, Ref.Metadata 5.0.0.0, Memory 4.0.1.1, Tasks.Extensions
// 4.2.0.1, Unsafe 4.0.6.0, CodePages 4.1.1.0, Buffers 4.0.3.0, Numerics.Vectors 4.1.4.0.
const fs = require('fs')
const path = require('path')

const DL = 'D:/DSH Unity/.roslyn-download'
const pkgs = [
  { id: 'microsoft.codeanalysis.csharp.scripting', ver: '3.8.0' },
  { id: 'microsoft.codeanalysis.csharp', ver: '3.8.0' },
  { id: 'microsoft.codeanalysis.common', ver: '3.8.0' },
  { id: 'microsoft.codeanalysis.scripting.common', ver: '3.8.0' },
  { id: 'system.text.encoding.codepages', ver: '4.5.1' },
  { id: 'system.collections.immutable', ver: '5.0.0' },
  { id: 'system.reflection.metadata', ver: '5.0.0' },
  { id: 'system.memory', ver: '4.5.4' },
  { id: 'system.buffers', ver: '4.5.1' },
  { id: 'system.numerics.vectors', ver: '4.5.0' },
  { id: 'system.runtime.compilerservices.unsafe', ver: '4.7.1' },
  { id: 'system.threading.tasks.extensions', ver: '4.5.4' },
]

fs.mkdirSync(DL, { recursive: true })

async function download(p) {
  const url = `https://api.nuget.org/v3-flatcontainer/${p.id}/${p.ver}/${p.id}.${p.ver}.nupkg`
  const res = await fetch(url)
  if (!res.ok) throw new Error(`${p.id}: HTTP ${res.status}`)
  const buf = Buffer.from(await res.arrayBuffer())
  const file = path.join(DL, `${p.id}.${p.ver}.nupkg`)
  fs.writeFileSync(file, buf)
  console.log(`OK ${p.id} ${p.ver} -> ${buf.length} bytes`)
}

;(async () => {
  const results = await Promise.allSettled(pkgs.map(download))
  const failed = results.filter((r) => r.status === 'rejected')
  for (const f of failed) console.error('FAIL', f.reason.message)
  if (failed.length) process.exit(1)
  console.log('all downloads complete')
})()
