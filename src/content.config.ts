import { z, defineCollection } from "astro:content";
import { glob } from "astro/loaders";

const events = defineCollection({
	loader: glob({
		pattern: "**/*.md",
		base: "./src/content/Events",
	}),
});

const history = defineCollection({
	loader: glob({
		pattern: "**/*.md",
		base: "./src/content/history",
	}),
	schema: ({ image }) =>
		z.object({
			date: z
				.string()
				.regex(/^(\d{4}|\d{4}-\d{2}|\d{4}-\d{2}-\d{2})$/, "Datum muss im Format yyyy, yyyy-mm oder yyyy-mm-dd angegeben werden."),
			title: z.string(),
			subtitle: z.string().optional(),
			image: image().optional(),
		}),
});

export const collections = {
	Events: events,
	history,
};
