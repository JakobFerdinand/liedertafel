import { defineCollection } from "astro:content";
import { glob } from "astro/loaders";

const events = defineCollection({
	loader: glob({
		pattern: "**/*.md",
		base: "./src/content/Events",
	}),
});

export const collections = {
	Events: events,
};
