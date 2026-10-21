import type { ImageMetadata } from "astro";
import type { render } from "astro:content";

export interface PrototypeEntry {
	id: string;
	date: string;
	formattedDate: string;
	title: string;
	subtitle?: string;
	image?: ImageMetadata;
	hasContent: boolean;
	Content: Awaited<ReturnType<typeof render>>["Content"];
}

export interface PrototypeGroup {
	title: string;
	rangeLabel: string;
	message?: string;
	entries: PrototypeEntry[];
}
