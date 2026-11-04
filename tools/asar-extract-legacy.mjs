// [已过时 / legacy] 第一轮手工摸 asar 结构时的一次性脚本，仅作历史留存。
// 注意：这里的 baseOffset = 16 + headerSize，少了数据区的 4 字节对齐，
// 对带 padding 的 asar（如 DSH 桌面版）会取错起点；参数也不做校验。
// 现在请用同仓库的 DeepSeekHarnessMenuPatch.exe（--dry-run 可只解析不写）。

// Minimal asar extractor (read-only) for inspecting the DSH bundle.
import fs from 'node:fs'
import path from 'node:path'

const asarPath = process.argv[2]
const outDir = process.argv[3]
const filters = process.argv.slice(4)

const fd = fs.openSync(asarPath, 'r')
const sizeBuf = Buffer.alloc(16)
fs.readSync(fd, sizeBuf, 0, 16, 0)
const headerSize = sizeBuf.readUInt32LE(12)
const headerBuf = Buffer.alloc(headerSize)
fs.readSync(fd, headerBuf, 0, headerSize, 16)
const header = JSON.parse(headerBuf.toString('utf8'))
const baseOffset = 16 + headerSize

function walk(node, prefix, cb) {
  for (const [name, entry] of Object.entries(node.files || {})) {
    const p = prefix ? `${prefix}/${name}` : name
    if (entry.files) walk(entry, p, cb)
    else if (entry.link) { /* skip */ }
    else cb(p, entry)
  }
}

const results = []
walk(header, '', (p, entry) => {
  if (filters.length === 0 || filters.some((f) => p.includes(f))) results.push([p, entry])
})
console.log(`matched ${results.length} entries`)
for (const [p, entry] of results) {
  const dest = path.join(outDir, p.replace(/[\\/]/g, '__'))
  fs.mkdirSync(path.dirname(dest), { recursive: true })
  const buf = Buffer.alloc(entry.size)
  fs.readSync(fd, buf, 0, entry.size, baseOffset + Number(entry.offset))
  fs.writeFileSync(dest, buf)
}
console.log(results.slice(0, 50).map(([p]) => p).join('\n'))
fs.closeSync(fd)
