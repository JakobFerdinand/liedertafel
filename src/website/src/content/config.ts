import { defineCollection } from "astro:content";
import { z } from "astro/zod";

const events = defineCollection({
	type: "content",
	schema: z.object({
		date: z.string(),
		name: z.string(),
		location: z.string(),
	}),
});

export const collections = { events };
