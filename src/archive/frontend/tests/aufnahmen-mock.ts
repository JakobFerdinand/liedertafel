import type { Page } from "@playwright/test";

/**
 * ARC-030: die Auftrittsseite lädt ihre Aufnahmen aus einer eigenen Liste.
 * Spezifikationen, die sich nicht um Aufnahmen drehen, stellen hier die
 * ehrliche leere Antwort von GET /api/events/{id}/recordings nach, damit der
 * Abschnitt nicht als Ladefehler erscheint. Eine später angemeldete,
 * genauere Route hat Vorrang.
 */
export async function mockLeereAufnahmen(page: Page) {
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
