import { defineConfig, svgoOptimizer } from "astro/config";
import sitemap from "@astrojs/sitemap";
import svelte from "@astrojs/svelte";

// https://astro.build/config
export default defineConfig({
	site: "https://liedertafel.at",
	integrations: [sitemap(), svelte()],
	experimental: {
		svgOptimizer: svgoOptimizer(),
	},
});
