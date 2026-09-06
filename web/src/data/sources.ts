import type { FightLog, IndexEntry } from '../schema/fightlog'
import { indexEntryFromLog, isLogFileName, parseIndex, parseLog } from './parse'

/** Where logs come from. Entries are listed eagerly; log bodies load on demand. */
export interface LogSource {
  kind: 'files' | 'directory' | 'sample' | 'url'
  label: string
  entries: IndexEntry[]
  loadLog(file: string): Promise<FightLog>
}

// ---- dropped / picked files ---------------------------------------------------

export async function filesSource(files: Iterable<File>): Promise<LogSource> {
  const logs = new Map<string, FightLog>()
  let index: IndexEntry[] | undefined
  const errors: string[] = []
  for (const file of files) {
    if (!file.name.endsWith('.json')) continue
    try {
      const text = await file.text()
      if (file.name === 'index.json') index = parseIndex(text)
      else if (isLogFileName(file.name)) logs.set(file.name, parseLog(text))
    } catch (err) {
      errors.push(`${file.name}: ${(err as Error).message}`)
    }
  }
  if (logs.size === 0 && !index) {
    throw new Error(errors.length ? errors.join('\n') : 'No fight-log JSON files found.')
  }
  // Prefer entries synthesized from the files we actually hold; a dropped
  // index.json may reference files that were not dropped.
  const entries = [...logs.entries()]
    .map(([name, log]) => indexEntryFromLog(log, name))
    .sort((a, b) => b.startedAt.localeCompare(a.startedAt))
  return {
    kind: 'files',
    label: `${logs.size} file${logs.size === 1 ? '' : 's'}`,
    entries: entries.length ? entries : (index ?? []),
    async loadLog(file) {
      const log = logs.get(file)
      if (!log) throw new Error(`${file} was not among the dropped files`)
      return log
    },
  }
}

/**
 * index.json written by plugin 0.4.0 before totalDamage/weapon existed has zeros; a
 * logged hunt never has 0 damage, so such rows are re-derived from the log itself.
 */
async function repairEntries(entries: IndexEntry[], load: (file: string) => Promise<FightLog>): Promise<IndexEntry[]> {
  const out: IndexEntry[] = []
  for (const entry of entries) {
    if (entry.totalDamage > 0) {
      out.push(entry)
      continue
    }
    try {
      out.push(indexEntryFromLog(await load(entry.file), entry.file))
    } catch {
      out.push(entry)
    }
  }
  return out
}

// ---- File System Access directory handle (Chromium) ---------------------------

export const supportsDirectoryPicker = typeof window !== 'undefined' && 'showDirectoryPicker' in window

export async function directorySource(handle: FileSystemDirectoryHandle): Promise<LogSource> {
  const files = new Map<string, FileSystemFileHandle>()
  for await (const [name, entry] of handle as unknown as AsyncIterable<[string, FileSystemHandle]>) {
    if (entry.kind === 'file' && name.endsWith('.json')) files.set(name, entry as FileSystemFileHandle)
  }

  const cache = new Map<string, FightLog>()
  const read = async (name: string) => (await files.get(name)!.getFile()).text()

  let entries: IndexEntry[] = []
  if (files.has('index.json')) {
    try {
      entries = parseIndex(await read('index.json')).filter((e) => files.has(e.file))
    } catch {
      entries = []
    }
  }
  if (entries.length === 0) {
    // No usable index: build one from the files (the plugin does the same on first run).
    for (const name of files.keys()) {
      if (!isLogFileName(name)) continue
      try {
        const log = parseLog(await read(name))
        cache.set(name, log)
        entries.push(indexEntryFromLog(log, name))
      } catch {
        // skip unreadable
      }
    }
    entries.sort((a, b) => b.startedAt.localeCompare(a.startedAt))
  }

  const loadLog = async (file: string) => {
    const cached = cache.get(file)
    if (cached) return cached
    if (!files.has(file)) throw new Error(`${file} is not in ${handle.name}`)
    const log = parseLog(await read(file))
    cache.set(file, log)
    return log
  }
  entries = await repairEntries(entries, loadLog)

  return { kind: 'directory', label: handle.name, entries, loadLog }
}

// ---- a served folder (the bundled sample, or any URL with an index.json) ------

export async function urlSource(base: string, label: string, kind: 'sample' | 'url' = 'url'): Promise<LogSource> {
  const root = base.endsWith('/') ? base : `${base}/`
  const res = await fetch(`${root}index.json`, { cache: 'no-store' })
  if (!res.ok) throw new Error(`${root}index.json: HTTP ${res.status}`)
  const cache = new Map<string, FightLog>()
  const loadLog = async (file: string) => {
    const cached = cache.get(file)
    if (cached) return cached
    const r = await fetch(`${root}${encodeURIComponent(file)}`, { cache: 'no-store' })
    if (!r.ok) throw new Error(`${file}: HTTP ${r.status}`)
    const log = parseLog(await r.text())
    cache.set(file, log)
    return log
  }
  const entries = await repairEntries(parseIndex(await res.text()), loadLog)
  return { kind, label, entries, loadLog }
}

export function sampleSource(): Promise<LogSource> {
  return urlSource(`${import.meta.env.BASE_URL}sample/`, 'Sample data', 'sample')
}
