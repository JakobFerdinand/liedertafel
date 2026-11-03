import assert from "node:assert/strict";
import { test } from "node:test";
import {
	SESSION_ID_KEY,
	VISITOR_ID_KEY,
	buildPageviewPayload,
	isProductionHost,
	navigationTypeFrom,
	pageviewBeaconBody,
	readOrCreateId,
	referrerHostOf,
	tryStorage,
	trimPath,
} from "./pageview-beacon.ts";

// Das Pageview-Beacon sendet POST /api/pageview beim Verlassen der Seite.
// Payload-Form und Speicher-Schlüssel decken mit dem Empfänger in
// src/website-api/features/pageviews/PageView.cs (Handler.Validate) überein.

class FakeStorage {
	private map = new Map<string, string>();
	getCount = 0;
	setCount = 0;
	failGet = false;
	failSet = false;

	getItem(key: string): string | null {
		this.getCount += 1;
		if (this.failGet) throw new Error("blocked");
		return this.map.get(key) ?? null;
	}

	setItem(key: string, value: string): void {
		this.setCount += 1;
		if (this.failSet) throw new Error("blocked");
		this.map.set(key, value);
	}
}

test("Die Speicher-Schlüssel bleiben unverändert lt-session und lt-visitor", () => {
	assert.equal(SESSION_ID_KEY, "lt-session");
	assert.equal(VISITOR_ID_KEY, "lt-visitor");
});

test("trimPath kürzt genau einen End-Slash und lässt \"/\" unangetastet", () => {
	assert.equal(trimPath("/termine/"), "/termine");
	assert.equal(trimPath("/x//"), "/x/"); // wie bisher ein Slash, nicht alle
	assert.equal(trimPath("/"), "/");
	assert.equal(trimPath("/x"), "/x");
	assert.equal(trimPath(""), "");
});

test("referrerHostOf liest den Host aus document.referrer, sonst leer", () => {
	assert.equal(referrerHostOf("https://www.google.at/search?q=x"), "www.google.at");
	assert.equal(referrerHostOf("https://liedertafel-mining.at/termine/"), "liedertafel-mining.at");
	assert.equal(referrerHostOf(""), "");
	assert.equal(referrerHostOf("keine url"), "");
});

test("readOrCreateId übernimmt eine vorhandene Id ohne Schreibzugriff", () => {
	const session = new FakeStorage();
	session.setItem("lt-session", "bestehend");
	assert.equal(readOrCreateId(session, SESSION_ID_KEY, () => {
		throw new Error("darf nicht erzeugt werden");
	}), "bestehend");
	assert.equal(session.setCount, 1); // nur das Setup oben
});

test("readOrCreateId erzeugt und schreibt eine fehlende Id", () => {
	const session = new FakeStorage();
	const created: string[] = [];
	assert.equal(readOrCreateId(session, SESSION_ID_KEY, () => {
		const id = "neu-1";
		created.push(id);
		return id;
	}), "neu-1");
	assert.deepEqual(created, ["neu-1"]);
	assert.equal(session.getCount, 1);
	assert.equal(session.setCount, 1);
});

test("readOrCreateId fällt beim blockierten Speicher auf leer zurück", () => {
	const failingGet = new FakeStorage();
	failingGet.failGet = true;
	assert.equal(readOrCreateId(failingGet, SESSION_ID_KEY, () => "nie"), "");

	const failingSet = new FakeStorage();
	failingSet.failSet = true;
	// Wie bisher: auch der erzeugte Wert verwirft sich, wenn das Schreiben scheitert.
	assert.equal(readOrCreateId(failingSet, VISITOR_ID_KEY, () => "neu"), "");
	assert.equal(failingSet.getItem(VISITOR_ID_KEY), null);

	assert.equal(readOrCreateId(undefined, SESSION_ID_KEY, () => "nie"), "");
});

test("navigationTypeFrom nimmt den ersten Navigationseintrag, sonst navigate", () => {
	assert.equal(navigationTypeFrom([]), "navigate");
	assert.equal(navigationTypeFrom([{ type: "reload" }]), "reload");
	assert.equal(navigationTypeFrom([{ type: "back_forward" }]), "back_forward");
	assert.equal(navigationTypeFrom([{ type: "navigate" }]), "navigate");
	assert.equal(navigationTypeFrom([{}]), "navigate"); // fehlender Typ bleibt beim Standard
	assert.equal(navigationTypeFrom([{ type: "back_forward" }, { type: "reload" }]), "back_forward");
});

test("isProductionHost erlaubt nur die echten Hosts", () => {
	assert.equal(isProductionHost("liedertafel-mining.at"), true);
	assert.equal(isProductionHost("www.liedertafel-mining.at"), true);
	assert.equal(isProductionHost("localhost"), false);
	assert.equal(isProductionHost("liedertafel-mining.at.b曹操.example"), false);
	assert.equal(isProductionHost(""), false);
});

test("buildPageviewPayload setzt das Payload zusammen wie der Empfänger es erwartet", () => {
	const session = new FakeStorage();
	session.setItem(SESSION_ID_KEY, "s1");
	const visitor = new FakeStorage();
	visitor.setItem(VISITOR_ID_KEY, "v1");

	const payload = buildPageviewPayload({
		pathname: "/termine/",
		referrer: "https://www.google.at/search",
		viewportWidth: 1024,
		sessionStorage: session,
		localStorage: visitor,
		navigationEntries: [{ type: "reload" }],
		createId: () => "nie",
	});

	assert.deepEqual(payload, {
		path: "/termine",
		referrerHost: "www.google.at",
		viewportWidth: 1024,
		sessionId: "s1",
		visitorId: "v1",
		navigationType: "reload",
	});
	// Feldreihenfolge und Werte gehen byte-identisch über die Leitung.
	assert.equal(
		JSON.stringify(payload),
		'{"path":"/termine","referrerHost":"www.google.at","viewportWidth":1024,"sessionId":"s1","visitorId":"v1","navigationType":"reload"}',
	);
});

test("buildPageviewPayload erzeugt fehlende Ids und leere Rückfälle", () => {
	const session = new FakeStorage();
	const visitor = new FakeStorage();
	let visits = 0;

	const payload = buildPageviewPayload({
		pathname: "/",
		referrer: "keine url",
		viewportWidth: 0,
		sessionStorage: session,
		localStorage: visitor,
		navigationEntries: [],
		createId: () => `id-${++visits}`,
	});

	assert.deepEqual(payload, {
		path: "/",
		referrerHost: "",
		viewportWidth: 0,
		sessionId: "id-1",
		visitorId: "id-2",
		navigationType: "navigate",
	});
	assert.equal(session.getItem(SESSION_ID_KEY), "id-1");
});

test("buildPageviewPayload sendet ohne blockierten Speicher mit leeren Ids", () => {
	assert.deepEqual(buildPageviewPayload({
		pathname: "/verein/",
		referrer: "",
		viewportWidth: 800,
	}), {
		path: "/verein",
		referrerHost: "",
		viewportWidth: 800,
		sessionId: "",
		visitorId: "",
		navigationType: "navigate",
	});
});

test("tryStorage fällt bei blockiertem Zugriff auf ohne Speicher zurück", () => {
	const session = new FakeStorage();
	assert.equal(tryStorage(() => session), session);
	assert.equal(tryStorage(() => undefined), undefined);
	assert.equal(tryStorage(() => {
		throw new Error("blocked");
	}), undefined);
});

test("pageviewBeaconBody verpackt das Payload als JSON-Blob", async () => {
	const body = pageviewBeaconBody({
		path: "/",
		referrerHost: "",
		viewportWidth: 1024,
		sessionId: "s1",
		visitorId: "v1",
		navigationType: "navigate",
	});
	assert.equal(body.type, "application/json");
	assert.equal(await body.text(), '{"path":"/","referrerHost":"","viewportWidth":1024,"sessionId":"s1","visitorId":"v1","navigationType":"navigate"}');
});
