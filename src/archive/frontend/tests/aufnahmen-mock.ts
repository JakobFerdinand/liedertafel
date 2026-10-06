import type { Page, Route } from "@playwright/test";

/**
 * ARC-030: die Auftrittsseite lädt ihre Aufnahmen aus einer eigenen Liste.
 * Spezifikationen, die sich nicht um Aufnahmen drehen, stellen hier die
 * ehrliche leere Antwort von GET /api/events/{id}/recordings nach, damit der
 * Abschnitt nicht als Ladefehler erscheint. Eine später angemeldete,
 * genauere Route hat Vorrang.
 */
export async function mockLeereAufnahmen(page: Page) {
  await mockLeerePassagen(page);
  await page.route("**/api/events/*/recordings", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const eventId = new URL(route.request().url()).pathname.split("/")[3];
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ eventId, recordings: [] }),
    });
  });
}

/**
 * ARC-032: jede Aufnahme lädt ihre Zeitmarken aus GET
 * /api/recordings/{id}/passages. Spezifikationen, die sich nicht um
 * Zeitmarken drehen, stellen hier die ehrliche leere Antwort nach (nichts
 * markiert). Eine später angemeldete, genauere Route hat Vorrang.
 */
export async function mockLeerePassagen(page: Page) {
  await page.route("**/api/recordings/*/passages", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const recordingId = new URL(route.request().url()).pathname.split("/")[3];
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        recordingId,
        eventId: "00000000-0000-0000-0000-000000000000",
        passages: [],
      }),
    });
  });
}

/** Echtes VP8-WebM, 64×36, 6 Sekunden (mit dem Playwright-ffmpeg erzeugt). */
export const webmBase64 =
  "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";

/**
 * Liefert Dateien des Speicherdienstes je Ticket-Adresse aus und beantwortet
 * Bereichsanfragen wie der echte Blob-Dienst (206 mit Content-Range) – darauf
 * stützt sich der Browser beim Suchen.
 */
export async function mockSpeicher(
  page: Page,
  dateien: Map<string, { body: Buffer | string; contentType: string }>,
) {
  await page.route("**/speicher.test/**", (route: Route) => {
    const eintrag = dateien.get(route.request().url());
    if (!eintrag) {
      return route.fulfill({
        status: 403,
        contentType: "text/plain",
        body: "abgelaufen",
      });
    }
    const inhalt = Buffer.from(eintrag.body);
    const bereich = /^bytes=(\d+)-(\d*)$/.exec(
      route.request().headers().range ?? "",
    );
    if (!bereich) {
      return route.fulfill({
        status: 200,
        contentType: eintrag.contentType,
        headers: { "Accept-Ranges": "bytes" },
        body: inhalt,
      });
    }
    const von = Number(bereich[1]);
    const bis = bereich[2] ? Number(bereich[2]) : inhalt.length - 1;
    return route.fulfill({
      status: 206,
      contentType: eintrag.contentType,
      headers: {
        "Accept-Ranges": "bytes",
        "Content-Range": `bytes ${von}-${bis}/${inhalt.length}`,
      },
      body: inhalt.subarray(von, bis + 1),
    });
  });
}
