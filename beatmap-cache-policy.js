'use strict';

const fs = require('fs');
const path = require('path');

const DAY_MS = 24 * 60 * 60 * 1000;
const DEFAULT_LIMITS = Object.freeze({
  maxFiles: 2000,
  maxBytes: 512 * 1024 * 1024,
  maxIdleMs: 90 * DAY_MS,
});

function positiveInteger(value, fallback) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? Math.floor(number) : fallback;
}

function cacheLimitsFromEnv(env) {
  env = env || process.env;
  return {
    maxFiles: positiveInteger(env.MINERADIO_BEAT_CACHE_MAX_FILES, DEFAULT_LIMITS.maxFiles),
    maxBytes: positiveInteger(env.MINERADIO_BEAT_CACHE_MAX_BYTES, DEFAULT_LIMITS.maxBytes),
    maxIdleMs: positiveInteger(env.MINERADIO_BEAT_CACHE_MAX_IDLE_DAYS, 90) * DAY_MS,
  };
}

function listCacheEntries(dir) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, { withFileTypes: true })
    .filter(entry => entry.isFile() && path.extname(entry.name).toLowerCase() === '.json')
    .map(entry => {
      const filePath = path.join(dir, entry.name);
      const stat = fs.statSync(filePath);
      return {
        filePath,
        bytes: stat.size || 0,
        lastAccessMs: Math.max(stat.atimeMs || 0, stat.mtimeMs || 0) || stat.birthtimeMs || 0,
      };
    });
}

function removeEntry(entry, report, reason) {
  fs.unlinkSync(entry.filePath);
  report.removedFiles += 1;
  report.removedBytes += entry.bytes;
  if (reason === 'expired') report.removedExpired += 1;
  else report.removedForLimits += 1;
}

function pruneBeatMapCacheDir(dir, options) {
  options = options || {};
  const limits = {
    maxFiles: positiveInteger(options.maxFiles, DEFAULT_LIMITS.maxFiles),
    maxBytes: positiveInteger(options.maxBytes, DEFAULT_LIMITS.maxBytes),
    maxIdleMs: positiveInteger(options.maxIdleMs, DEFAULT_LIMITS.maxIdleMs),
  };
  const now = Number.isFinite(options.now) ? options.now : Date.now();
  const report = {
    reason: options.reason || 'manual',
    scannedFiles: 0,
    removedFiles: 0,
    removedBytes: 0,
    removedExpired: 0,
    removedForLimits: 0,
    remainingFiles: 0,
    remainingBytes: 0,
    finishedAt: now,
  };

  let entries = listCacheEntries(dir);
  report.scannedFiles = entries.length;
  const active = [];
  entries.forEach(entry => {
    if (now - entry.lastAccessMs > limits.maxIdleMs) removeEntry(entry, report, 'expired');
    else active.push(entry);
  });

  entries = active.sort((a, b) => a.lastAccessMs - b.lastAccessMs);
  let totalBytes = entries.reduce((sum, entry) => sum + entry.bytes, 0);
  while (entries.length > limits.maxFiles || totalBytes > limits.maxBytes) {
    const entry = entries.shift();
    removeEntry(entry, report, 'limit');
    totalBytes -= entry.bytes;
  }

  report.remainingFiles = entries.length;
  report.remainingBytes = totalBytes;
  return report;
}

function touchBeatMapCacheFile(filePath, now) {
  const stat = fs.statSync(filePath);
  fs.utimesSync(filePath, new Date(Number.isFinite(now) ? now : Date.now()), stat.mtime);
}

module.exports = {
  DEFAULT_LIMITS,
  cacheLimitsFromEnv,
  pruneBeatMapCacheDir,
  touchBeatMapCacheFile,
};
