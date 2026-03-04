import { defineCollection, z } from "astro:content";

const events = defineCollection({
	type: "content",
	schema: z.object({
		date: z.string(),
		name: z.string(),
		location: z.string(),
	}),
});

export const collections = { events };
