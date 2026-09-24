import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const website = fileURLToPath(new URL("..", import.meta.url));
const repository = fileURLToPath(new URL("../..", import.meta.url));
const origin = "https://scrap.moesegfault.dev";

/** Read PNG dimensions without adding an image dependency. / 不引入图片依赖即可读取 PNG 尺寸。 */
function pngSize(path) {
  const bytes = readFileSync(path);
  expect(bytes.subarray(0, 8).toString("hex")).toBe("89504e470d0a1a0a");
  return [bytes.readUInt32BE(16), bytes.readUInt32BE(20)];
}

/** Compare published copies to their canonical source. / 将发布副本与权威源文件逐字节比较。 */
function expectSameFile(source, published) {
  const digest = (path) => createHash("sha256").update(readFileSync(path)).digest("hex");
  expect(digest(published), published).toBe(digest(source));
}

/** Protect manually published metadata and copied visuals from silent drift. / 防止手工发布元数据与复制素材悄然偏离。 */
describe("static publication assets", () => {
  it("publishes every localized page in the sitemap", () => {
    const pages = join(website, "src", "pages");
    const routes = ["", "en"].flatMap((locale) =>
      readdirSync(join(pages, locale)).filter((file) => file.endsWith(".astro")).map((file) => {
        const leaf = file === "index.astro" ? "" : file.replace(/\.astro$/, "/");
        return `${origin}/${locale ? `${locale}/` : ""}${leaf}`;
      }),
    );
    const sitemap = readFileSync(join(website, "public", "sitemap-0.xml"), "utf8");
    const published = [...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map((match) => match[1]);

    expect(published).toHaveLength(routes.length);
    expect(new Set(published)).toEqual(new Set(routes));
    expect(readFileSync(join(website, "public", "CNAME"), "utf8").trim()).toBe(new URL(origin).hostname);
  });

  it("publishes unmodified brand-icon exports", () => {
    for (const size of [32, 64, 256, 512]) {
      const name = `scrap-icon-${size}.png`;
      const source = join(repository, "assets", "branding", name);
      const published = join(website, "public", name);
      expect(pngSize(published)).toEqual([size, size]);
      expectSameFile(source, published);
    }
  });

  it("publishes the matching localized Store captures without altering pixels", () => {
    for (const locale of ["en-US", "zh-CN"]) {
      for (const [sourceName, publishedName] of [
        ["02-multi-scope-subset.png", "multi-scope-search.png"],
        ["03-create-record.png", "create-record.png"],
      ]) {
        const source = join(repository, "store-listing", "screenshots", locale, sourceName);
        const published = join(website, "public", "screenshots", locale, publishedName);
        expect(pngSize(published)).toEqual([1366, 768]);
        expectSameFile(source, published);
      }
    }
  });

  it("keeps the Store submission media inventory complete", () => {
    const listing = join(repository, "store-listing");
    expect(pngSize(join(listing, "assets", "AppTileIcon-300x300.png"))).toEqual([300, 300]);
    for (const locale of ["en-US", "zh-CN"]) {
      const screenshots = readdirSync(join(listing, "screenshots", locale)).filter((file) => file.endsWith(".png"));
      expect(screenshots).toEqual([
        "01-all-scopes-masked.png",
        "02-multi-scope-subset.png",
        "03-create-record.png",
        "04-temporary-reveal.png",
      ]);
      for (const screenshot of screenshots) {
        expect(pngSize(join(listing, "screenshots", locale, screenshot))).toEqual([1366, 768]);
      }
    }
  });
});
