import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import {
  speicherTierDatum,
  speicherTierText,
  verschiebeSpeicherTier,
} from "./speicher.ts";

// Reine Helfer und ein abgehorchtes fetch: kein React, kein Browser.

type AbgefangeneAnfrage = { pfad: string; init: RequestInit };

const echtesFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = echtesFetch;
});

/** Ersetzt fetch: der CSRF-Abruf antwortet, jeder POST wird mitgeschrieben. */
function horcheFetch(post: (anfrage: AbgefangeneAnfrage) => Response): void {
  globalThis.fetch = (async (
    pfad: string | URL | globalThis.Request,
    init?: RequestInit,
  ) => {
    const adresse = String(pfad);
    if (adresse === "/api/antiforgery") {
      return new Response(JSON.stringify({ token: "pruef" }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    }
    return post({ pfad: adresse, init: init ?? {} });
  }) as unknown as typeof fetch;
}

test("Die Verschiebung schickt Ziel-Stufe und Sicherheitspaar und liest die Antwort", async () => {
  const gefangen: AbgefangeneAnfrage[] = [];
  horcheFetch((anfrage) => {
    gefangen.push(anfrage);
    return new Response(
      JSON.stringify({
        datei: { id: "d1", storageTier: "cold", tierChangedAt: null },
      }),
      { status: 200, headers: { "content-type": "application/json" } },
    );
  });

  const antwort = await verschiebeSpeicherTier("d1", "cold");

  assert.equal(gefangen.length, 1);
  const anfrage = gefangen.at(-1);
  assert.ok(anfrage);
  assert.equal(anfrage.pfad, "/api/dateien/d1/storage-tier");
  assert.equal(anfrage.init.method, "POST");
  assert.equal(anfrage.init.credentials, "same-origin");
  const kopf = new Headers(anfrage.init.headers);
  assert.equal(kopf.get("content-type"), "application/json");
  assert.equal(kopf.get("x-csrf-token"), "pruef");
  assert.deepEqual(JSON.parse(String(anfrage.init.body)), {
    tier: "cold",
  });
  assert.equal(antwort.datei.storageTier, "cold");
  assert.equal(antwort.datei.id, "d1");
});

test("Die Verschiebung schickt die rowVersion mit und kodiert die Kennung", async () => {
  const gefangen: AbgefangeneAnfrage[] = [];
  horcheFetch((anfrage) => {
    gefangen.push(anfrage);
    return new Response(
      JSON.stringify({
        datei: { id: "d/1", storageTier: "hot", tierChangedAt: null },
      }),
      { status: 200, headers: { "content-type": "application/json" } },
    );
  });

  await verschiebeSpeicherTier("d/1", "hot", 7);

  const anfrage = gefangen.at(-1);
  assert.ok(anfrage);
  assert.equal(anfrage.pfad, "/api/dateien/d%2F1/storage-tier");
  assert.deepEqual(JSON.parse(String(anfrage.init.body)), {
    tier: "hot",
    rowVersion: 7,
  });
});

test("Eine veraltete rowVersion kommt als geworfene Antwort an", async () => {
  horcheFetch(
    () =>
      new Response(
        JSON.stringify({
          title: "Der Eintrag wurde zwischenzeitlich geändert.",
        }),
        {
          status: 409,
          headers: { "content-type": "application/problem+json" },
        },
      ),
  );

  await assert.rejects(
    verschiebeSpeicherTier("d1", "cold", 3),
    (fehler: unknown): boolean =>
      fehler instanceof Response && fehler.status === 409,
  );
});

test("Die Zustandszeile nennt Stufe und, wenn vorhanden, das Datum", () => {
  assert.equal(speicherTierText("cold"), "Original liegt im Cold Storage.");
  assert.equal(
    speicherTierText("cold", "2026-11-03T09:30:00Z"),
    "Original liegt seit 3.11.2026 im Cold Storage.",
  );
  assert.equal(speicherTierText("hot"), "Original liegt im Hot Storage.");
  assert.equal(
    speicherTierText("hot", "2026-11-03T09:30:00Z"),
    "Original liegt seit 3.11.2026 im Hot Storage.",
  );
});

test("Ohne oder mit unlesbarem Datum bleibt das Datum weg", () => {
  assert.equal(speicherTierDatum(null), null);
  assert.equal(speicherTierDatum(undefined), null);
  assert.equal(speicherTierDatum("nicht-datum"), null);
});
