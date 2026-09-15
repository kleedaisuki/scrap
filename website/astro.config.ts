import { defineConfig } from "astro/config";

/** GitHub Pages static-site configuration. / GitHub Pages 静态站点配置。 */
export default defineConfig({
  site: "https://scrap.moesegfault.dev",
  output: "static",
  trailingSlash: "always",
});
