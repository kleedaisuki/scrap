import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const stylesheet = readFileSync(new URL("../src/styles/global.css", import.meta.url), "utf8");

/** Guards responsive product screenshots against width/height hint distortion. / 防止响应式产品截图被宽高提示拉伸。 */
describe("responsive product screenshots", () => {
  it("lets screenshot height follow the responsive width", () => {
    const rule = stylesheet.match(/\.product-shot img\s*\{([^}]*)\}/)?.[1];

    expect(rule).toBeDefined();
    expect(rule).toMatch(/\bwidth:\s*100%\s*;/);
    expect(rule).toMatch(/\bheight:\s*auto\s*;/);
  });
});
