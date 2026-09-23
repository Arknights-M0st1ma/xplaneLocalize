// Manual library: the PDFs live on the PC (the iPad cannot see its filesystem),
// so the bridge lists them and streams them back. Everything here is a pure
// function over a folder path, which is what makes it testable.

import fs from 'node:fs';
import path from 'node:path';

export const MAX_MANUALS = 600;
const PDF_EXTENSION = '.pdf';

/** Rejects anything that tries to leave the configured folder. */
export function resolveInside(root, relative) {
  if (typeof root !== 'string' || root.length === 0) return null;
  if (typeof relative !== 'string' || relative.length === 0) return null;
  // A NUL byte or an absolute/drive-qualified path is never a legitimate name.
  if (relative.includes('\0')) return null;
  if (path.isAbsolute(relative) || /^[a-zA-Z]:/.test(relative)) return null;
  const base = path.resolve(root);
  const target = path.resolve(base, relative);
  if (target !== base && !target.startsWith(`${base}${path.sep}`)) return null;
  return target;
}

/**
 * Walks the manual folder (sub-folders included) and returns the PDFs.
 * Unreadable sub-folders are skipped rather than failing the whole listing.
 */
export function listManuals(folder, { limit = MAX_MANUALS } = {}) {
  const result = { configured: false, folder: folder ?? '', files: [], truncated: false };
  if (typeof folder !== 'string' || folder.trim().length === 0) return result;
  result.folder = folder;
  let root;
  try {
    root = fs.realpathSync(folder);
  } catch {
    result.error = '找不到手册文件夹';
    return result;
  }
  result.configured = true;
  const found = [];
  const walk = (directory, depth) => {
    if (depth > 6 || found.length > limit) return;
    let entries;
    try {
      entries = fs.readdirSync(directory, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      if (found.length > limit) return;
      if (entry.name.startsWith('.')) continue;
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(absolute, depth + 1);
        continue;
      }
      if (!entry.isFile() || path.extname(entry.name).toLowerCase() !== PDF_EXTENSION) continue;
      let stat;
      try {
        stat = fs.statSync(absolute);
      } catch {
        continue;
      }
      found.push({
        path: path.relative(root, absolute).split(path.sep).join('/'),
        name: path.basename(entry.name, PDF_EXTENSION),
        size: stat.size,
        modifiedAt: Math.round(stat.mtimeMs)
      });
    }
  };
  walk(root, 0);
  found.sort((a, b) => a.path.localeCompare(b.path, 'zh-Hans-CN'));
  result.truncated = found.length > limit;
  result.files = found.slice(0, limit);
  return result;
}

/** The absolute path of one manual, or null when it is not a servable PDF. */
export function manualFile(folder, relative) {
  const target = resolveInside(folder, relative);
  if (!target) return null;
  if (path.extname(target).toLowerCase() !== PDF_EXTENSION) return null;
  let stat;
  try {
    stat = fs.statSync(target);
  } catch {
    return null;
  }
  if (!stat.isFile()) return null;
  // Follow symlinks only inside the configured folder.
  try {
    const real = fs.realpathSync(target);
    const root = fs.realpathSync(folder);
    if (real !== root && !real.startsWith(`${root}${path.sep}`)) return null;
    return { absolute: real, size: stat.size };
  } catch {
    return null;
  }
}

/**
 * Single-range parser for the `Range: bytes=…` header. PDF.js asks for byte
 * ranges so it can jump into a large manual without downloading all of it.
 * Returns null when the header is absent or unusable (caller then sends 200).
 */
export function parseRange(header, size) {
  if (typeof header !== 'string' || !Number.isFinite(size) || size <= 0) return null;
  const match = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!match) return null;
  const [, rawStart, rawEnd] = match;
  if (rawStart === '' && rawEnd === '') return null;
  let start;
  let end;
  if (rawStart === '') {
    // "bytes=-500" is the last 500 bytes.
    const length = Number(rawEnd);
    if (!Number.isFinite(length) || length <= 0) return null;
    start = Math.max(0, size - length);
    end = size - 1;
  } else {
    start = Number(rawStart);
    end = rawEnd === '' ? size - 1 : Number(rawEnd);
  }
  if (!Number.isInteger(start) || !Number.isInteger(end)) return null;
  if (start < 0 || start >= size) return null;
  if (end >= size) end = size - 1;
  if (end < start) return null;
  return { start, end, length: end - start + 1 };
}
