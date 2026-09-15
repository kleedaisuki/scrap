import { describe, expect, it } from "vitest";
import { copy } from "../src/lib/content";
import { privacyCopy } from "../src/lib/privacy";
import { supportCopy } from "../src/lib/support";

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

  it("provides localized context for real product screenshots", () => {
    for (const locale of Object.values(copy)) {
      expect(locale.showcase.scopeAlt.length).toBeGreaterThan(30);
      expect(locale.showcase.createAlt.length).toBeGreaterThan(30);
      expect(locale.showcase.scopeCaption).toBeTruthy();
      expect(locale.showcase.createCaption).toBeTruthy();
    }
  });
});

/** Guards the bilingual policy's structure and material disclosures. / 守护双语政策的结构与关键披露。 */
describe("localized privacy policy", () => {
  it("keeps equivalent sections and stable anchors in both locales", () => {
    expect(privacyCopy.zh.sections.map(({ id }) => id)).toEqual(privacyCopy.en.sections.map(({ id }) => id));
    expect(privacyCopy.zh.sections).toHaveLength(8);
  });

  it("states encryption, clipboard, uninstall, and external boundaries", () => {
    const english = JSON.stringify(privacyCopy.en);
    for (const disclosure of ["Unencrypted metadata", "30 seconds", "does not automatically clear", "preserve data under ~/.scrap", "Operating-system and external boundaries"]) {
      expect(english).toContain(disclosure);
    }
  });
});

/** Guards support parity and the public-channel warning. / 守护支持页双语对等和公开渠道警告。 */
describe("localized product support", () => {
  it("keeps equivalent topic anchors in both locales", () => {
    expect(supportCopy.zh.topics.map(({ id }) => id)).toEqual(supportCopy.en.topics.map(({ id }) => id));
    expect(supportCopy.en.topics.map(({ id }) => id)).toEqual(["install", "start", "data", "cli"]);
  });

  it("documents aliases, preserved data, and safe public contact", () => {
    const english = JSON.stringify(supportCopy.en);
    for (const disclosure of ["App Execution Alias", "%LOCALAPPDATA%", "does not remove this directory", "entirely public", "fully redacted contact request"]) {
      expect(english).toContain(disclosure);
    }
  });
});
