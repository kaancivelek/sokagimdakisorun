// BFF sınırı (ADR-0006): tarayıcıda çalışan kod .NET API'yi, ortam değişkenlerini veya oturumu görmez.
// Statik denetim: "use client" dosyaları ve NEXT_PUBLIC_ kullanımı.
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { describe, it } from "node:test";

const ROOT = join(import.meta.dirname);

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return sourceFiles(path);
    return /\.(ts|tsx)$/.test(name) && !name.endsWith(".test.ts") && !name.endsWith(".d.ts") ? [path] : [];
  });
}

const files = sourceFiles(ROOT).map((path) => ({ path: relative(ROOT, path), text: readFileSync(path, "utf8") }));
const clientFiles = files.filter((f) => /^\s*["']use client["']/m.test(f.text));

describe("BFF boundary", () => {
  it("has client components to check", () => {
    assert.ok(clientFiles.length >= 3);
  });

  for (const file of clientFiles) {
    it(`${file.path} does not call the API or read secrets`, () => {
      for (const forbidden of [
        /\bfetch\s*\(/,
        /process\.env/,
        /API_BASE_URL|SESSION_SECRET/,
        /\/api\/v1\//,
        /from\s+["']@\/lib\/(api\/client|session\/(server|crypto|lifecycle)|env)["']/,
        /accessToken|refreshToken/,
      ]) {
        assert.doesNotMatch(file.text, forbidden);
      }
    });
  }

  it("no NEXT_PUBLIC_ variables are used", () => {
    for (const file of files) assert.doesNotMatch(file.text, /process\.env\.NEXT_PUBLIC_|env\[["']NEXT_PUBLIC_/, file.path);
  });

  it("server-only modules are guarded", () => {
    const server = files.find((f) => f.path.replace(/\\/g, "/") === "lib/session/server.ts");
    assert.match(server!.text, /^import "server-only";/m);
  });

  it("user input is never rendered as HTML", () => {
    for (const file of files) assert.doesNotMatch(file.text, /dangerouslySetInnerHTML/, file.path);
  });
});
