import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiError } from "./api-client.ts";
import { createLoader, type LoaderConfig, type LoaderRequest, type LoaderView } from "./loader.ts";

type PopstateListener = () => void;

class FakeWindow {
	events: PopstateListener[] = [];
	search: string;
	constructor(search: string) { this.search = search; }
	addEventListener(type: "popstate", listener: PopstateListener) { if (type === "popstate") this.events.push(listener); }
	removeEventListener(_type: "popstate", listener: PopstateListener) { this.events = this.events.filter((current) => current !== listener); }
	popstate() { for (const listener of [...this.events]) listener(); }
}

function deferred(): { promise: Promise<unknown>; resolve: (value: unknown) => void; reject: (error: unknown) => void } {
	let resolve!: (value: unknown) => void;
	let reject!: (error: unknown) => void;
	const promise = new Promise<unknown>((done, fail) => { resolve = done; reject = fail; });
	return { promise, resolve, reject };
}

type Gate = { resolve: (value: unknown) => void; reject: (error: unknown) => void };

function harness<T>(config: Partial<LoaderConfig<T>> = {}, search = "start=2026-01-01&end=2026-01-07&granularity=day&compare=0") {
	const inline = new FakeWindow(search);
	const writes: string[] = [];
	const requests: LoaderRequest<T>[] = [];
	const gates: Gate[] = [];
	const loader = createLoader<T>({
		window: inline,
		write: (rewritten) => writes.push(new URLSearchParams(rewritten).toString()),
		fetch: (request) => {
			requests.push({ ...request });
			const gate = deferred();
			gates.push(gate);
			return gate.promise as Promise<T>;
		},
		...config
	});
	const settle = () => new Promise<void>((done) => { setTimeout(done, 0); });
	const view = (): LoaderView<T> => loader.read();
	return { loader, window: inline, writes, requests, gates, settle, view };
}

test("connect starts an initial load from the ambient URL and settles its state", async () => {
	const { loader, requests, writes, gates, settle, view } = harness<number>();
	const stop = loader.connect();
	assert.equal(view().loading, true);
	assert.equal(view().error, null);
	assert.equal(view().stale, true);
	assert.equal(requests.length, 1);
	assert.ok(requests[0].params.get("start") === "2026-01-01");
	assert.ok(requests[0].params.get("compare") === "0");
	assert.deepEqual(view().range, { start: "2026-01-01", end: "2026-01-07", granularity: "day", compare: false });
	assert.equal(writes.length, 1);
	assert.equal(writes[0], "start=2026-01-01&end=2026-01-07&granularity=day&compare=0");
	gates[0].resolve(5);
	await settle();
	assert.equal(view().loading, false);
	assert.equal(view().stale, false);
	assert.equal(view().data, 5);
	assert.equal(view().shown.toString(), "start=2026-01-01&end=2026-01-07&granularity=day&compare=0");
	stop();
});

test("a pre-flight validation error skips the fetch but still writes the URL", () => {
	let verdict: string | null = "Der Zeitraum ist zu lang.";
	const { loader, requests, writes, view } = harness<number>({ validate: () => verdict });
	loader.connect();
	assert.equal(writes.length, 1);
	assert.equal(requests.length, 0);
	assert.equal(view().error, "Der Zeitraum ist zu lang.");
	assert.equal(view().loading, false);
	assert.equal(view().stale, true);
	assert.equal(view().data, null);
	verdict = null;
	void loader.load(new URLSearchParams("start=2026-02-01&end=2026-02-07"));
	assert.equal(view().error, null);
	assert.equal(view().loading, true);
	assert.equal(requests.length, 1);
});

test("a newer load aborts the older one and only the winner's result lands", async () => {
	const { loader, requests, gates, settle, view } = harness<number>();
	loader.connect();
	void loader.load(new URLSearchParams("start=2026-02-20&end=2026-02-26"));
	assert.equal(view().loading, true);
	assert.equal(requests.length, 2);
	assert.equal(requests[0].signal.aborted, true);
	assert.equal(requests[1].signal.aborted, false);
	gates[0].resolve(7);
	await settle();
	assert.equal(view().loading, true);
	assert.equal(view().data, null);
	gates[1].resolve(11);
	await settle();
	assert.equal(view().loading, false);
	assert.equal(view().data, 11);
	assert.equal(view().shown.toString(), "start=2026-02-20&end=2026-02-26&granularity=day&compare=1");
	gates[0].resolve(9);
	await settle();
	assert.equal(view().data, 11);
	assert.equal(view().loading, false);
	assert.equal(view().error, null);
});

test("popstate reloads the ambient URL over the current state", async () => {
	const { loader, window, requests, writes, gates, settle, view } = harness<number>();
	loader.connect();
	gates[0].resolve(5);
	await settle();
	window.search = "start=2026-03-01&end=2026-03-31&granularity=week";
	window.popstate();
	assert.equal(view().loading, true);
	assert.equal(view().data, 5);
	assert.equal(requests.length, 2);
	assert.equal(requests[1].signal.aborted, false);
	assert.deepEqual(view().range, { start: "2026-03-01", end: "2026-03-31", granularity: "week", compare: true });
	assert.equal(writes.length, 2);
	assert.equal(writes[1], "start=2026-03-01&end=2026-03-31&granularity=week&compare=1");
	gates[1].resolve(6);
	await settle();
	assert.equal(view().loading, false);
	assert.equal(view().data, 6);
	assert.equal(view().shown.toString(), "start=2026-03-01&end=2026-03-31&granularity=week&compare=1");
});

test("cleanup aborts the active load and unregisters the popstate listener", async () => {
	const { loader, window, requests, gates, settle, view } = harness<number>();
	const stop = loader.connect();
	void loader.load(new URLSearchParams("start=2026-02-20&end=2026-02-26"));
	assert.equal(requests.length, 2);
	assert.equal(window.events.length, 1);
	stop();
	assert.equal(requests[1].signal.aborted, true);
	assert.equal(window.events.length, 0);
	window.search = "start=2026-04-01&end=2026-04-07";
	window.popstate();
	assert.equal(requests.length, 2);
	gates[1].resolve(3);
	await settle();
	assert.equal(view().data, null);
	assert.equal(view().loading, true);
	void loader.load(new URLSearchParams("start=2026-02-20&end=2026-02-26"));
	assert.equal(requests.length, 3);
	assert.equal(requests[2].signal.aborted, false);
});

test("failures map to message and kind while the shown result is kept", async () => {
	const { loader, requests, gates, settle, view } = harness<number>({ fallback: "Sitzungen konnten nicht geladen werden." });
	loader.connect();
	gates[0].resolve(4);
	await settle();
	void loader.load();
	let gate = gates[1];
	gate.reject(new Error("Die Verbindung ist unterbrochen."));
	await settle();
	assert.equal(view().error, "Die Verbindung ist unterbrochen.");
	assert.equal(view().failed, null);
	void loader.load();
	gate = gates[2];
	gate.reject(new ApiError("expired", "Der Zeitraum ist abgelaufen."));
	await settle();
	assert.equal(view().error, "Der Zeitraum ist abgelaufen.");
	assert.equal(view().failed, "expired");
	void loader.load();
	gate = gates[3];
	gate.reject(42);
	await settle();
	assert.equal(view().error, "Sitzungen konnten nicht geladen werden.");
	assert.equal(view().failed, null);
	assert.equal(view().data, 4);
	assert.equal(view().loading, false);
	assert.equal(view().stale, true);
	assert.equal(requests.length, 4);
});

test("load without arguments replays the current parameters and append passes through", async () => {
	const { loader, requests, gates, settle } = harness<number>();
	loader.connect();
	gates[0].resolve(1);
	await settle();
	void loader.load();
	assert.equal(requests.length, 2);
	assert.equal(requests[1].previous, 1);
	assert.equal(requests[1].append, false);
	assert.equal(requests[1].params.toString(), requests[0].params.toString());
	gates[1].resolve(2);
	await settle();
	void loader.load(new URLSearchParams("start=2026-02-20&end=2026-02-26"), true);
	assert.equal(requests[2].append, true);
	assert.equal(requests[2].previous, 2);
});

test("the rewrite seam decides the written URL shape while the fetch sees the merged params", async () => {
	const { loader, requests, writes, gates, settle } = harness<number>({ rewrite: (_params, range) => new URLSearchParams({ start: range.start, end: range.end }) });
	loader.connect();
	assert.equal(writes[0], "start=2026-01-01&end=2026-01-07");
	assert.ok(requests[0].params.get("granularity") === "day");
	gates[0].resolve(1);
	await settle();
});
