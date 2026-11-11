// Summarises a benchmark result file: median across runs of each run's p50 and p95.
// Usage: node benchmarks/summarize.mjs benchmark-results/rls-overhead-<timestamp>.json
import { readFileSync } from "node:fs";

const file = process.argv[2];
if (!file) {
  console.error("usage: node benchmarks/summarize.mjs <result.json>");
  process.exit(2);
}

const data = JSON.parse(readFileSync(file, "utf8"));
const median = (xs) => {
  const s = [...xs].sort((a, b) => a - b);
  const m = Math.floor(s.length / 2);
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
};
const fmt = (x) => x.toFixed(3);

const groups = new Map();
for (const r of data.results) {
  const key = `${r.part}|${r.layout}|${r.query}`;
  if (!groups.has(key)) groups.set(key, []);
  groups.get(key).push(r);
}

console.log(`rows per table: ${data.rows}, runs: ${data.runs}, server: ${data.server.split(" on ")[0]}\n`);
console.log("| Layout | Query | no RLS p50 (ms) | RLS p50 (ms) | overhead p50 | no RLS p95 | RLS p95 |");
console.log("|---|---|---:|---:|---:|---:|---:|");
for (const [, rs] of groups) {
  if (rs[0].part !== "A") continue;
  const pp50 = median(rs.map((r) => r.plain.P50));
  const rp50 = median(rs.map((r) => r.rls.P50));
  const pp95 = median(rs.map((r) => r.plain.P95));
  const rp95 = median(rs.map((r) => r.rls.P95));
  const over = ((rp50 - pp50) / pp50) * 100;
  console.log(`| ${rs[0].layout} | ${rs[0].query} | ${fmt(pp50)} | ${fmt(rp50)} | ${over >= 0 ? "+" : ""}${over.toFixed(1)}% | ${fmt(pp95)} | ${fmt(rp95)} |`);
}

console.log("\n| Transaction shape (1,000-tenant table, latest 50) | p50 (ms) | p95 (ms) |");
console.log("|---|---:|---:|");
for (const [, rs] of groups) {
  if (rs[0].part !== "B") continue;
  console.log(`| ${rs[0].query} | ${fmt(median(rs.map((r) => r.stats.P50)))} | ${fmt(median(rs.map((r) => r.stats.P95)))} |`);
}
