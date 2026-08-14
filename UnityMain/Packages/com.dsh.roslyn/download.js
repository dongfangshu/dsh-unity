// Download Roslyn 3.8.0 + runtime deps from nuget.org (flat container) via Node fetch.
// schannel (pwsh/curl) cannot establish TLS on this machine; Node's OpenSSL works.
const fs = require('fs')
const path = require('path')

const DL = 'D:/DSH Unity/.roslyn-download'
const pkgs = [
  { id: 'microsoft.codeanalysis.csharp.scripting', ver: '3.8.0' },
  { id: 'microsoft.codeanalysis.csharp', ver: '3.8.0' },
  { id: 'microsoft.codeanalysis.common', ver: '3.8.0' },
  { id: 'system.text.encoding.codepages', ver: '4.5.1' },
  { id: 'system.collections.immutable', ver: '1.5.0' },
  { id: 'system.reflection.metadata', ver: '1.6.0' },
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
