import { describe, expect, it } from "vitest";
import { copy } from "../src/lib/content";

/** Ensures both localized release pages stay structurally equivalent. / 确保双语发布页结构保持一致。 */
describe("localized product copy", () => {
  it("keeps feature and platform counts aligned", () => {
    expect(copy.zh.features).toHaveLength(copy.en.features.length);
    expect(copy.zh.download.platforms).toHaveLength(copy.en.download.platforms.length);
    expect(copy.zh.architecture.nodes).toHaveLength(copy.en.architecture.nodes.length);
  });

  it("provides meaningful metadata in every locale", () => {
    for (const locale of Object.values(copy)) {
      expect(locale.meta.title.length).toBeGreaterThan(10);
      expect(locale.meta.description.length).toBeGreaterThan(60);
      expect(locale.hero.primary).toBeTruthy();
    }
  });
});
