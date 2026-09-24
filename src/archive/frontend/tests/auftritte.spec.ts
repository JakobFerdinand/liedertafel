import { expect, type Page, test } from "@playwright/test";

const editorMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000001",
  email: "redaktion@liedertafel.test",
  displayName: "Redaktion",
  roles: ["Editor"],
  verifiedAt: new Date().toISOString(),
};

const memberMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000002",
  email: "mitglied@liedertafel.test",
  displayName: "Testmitglied",
  roles: ["Member"],
  verifiedAt: new Date().toISOString(),
};

const konzertId = "00000000-0000-0000-0000-00000000e001";
const umJahrId = "00000000-0000-0000-0000-00000000e002";
const ohneDatumId = "00000000-0000-0000-0000-00000000e003";
const entwurfId = "00000000-0000-0000-0000-00000000e004";

const konzert = {
  id: konzertId,
  kind: "concert",
  title: "Frühlingskonzert",
  venue: "Stadtsaal Mining",
  dateYear: 1950,
  dateMonth: 5,
  dateDay: 12,
  dateApproximate: false,
  datePrecision: "day",
  dateDisplay: "12. Mai 1950",
  startTime: "19:30",
  published: true,
};

const umJahr = {
  id: umJahrId,
  kind: "festival",
  title: "Sängerfest",
  venue: null,
  dateYear: 1950,
  dateMonth: null,
  dateDay: null,
  dateApproximate: true,
  datePrecision: "year",
  dateDisplay: "um 1950",
  startTime: null,
  published: true,
};

const ohneDatum = {
  id: ohneDatumId,
  kind: "service",
  title: "Gedenkgottesdienst",
  venue: "Pfarrkirche Mining",
  dateYear: null,
  dateMonth: null,
  dateDay: null,
  dateApproximate: false,
  datePrecision: "unknown",
  dateDisplay: "Datum unbekannt",
  startTime: null,
  published: true,
};

const entwurf = {
  id: entwurfId,
  kind: "wedding",
  title: "Hochzeit der Familie Berger",
  venue: "Pfarrkirche Mining",
  dateYear: 1951,
  dateMonth: 6,
  dateDay: 2,
  dateApproximate: false,
  datePrecision: "day",
  dateDisplay: "2. Juni 1951",
  startTime: "10:00",
  published: false,
};

const umJahrDetail = {
  event: {
    ...umJahr,
    notes: "Das genaue Datum ist nicht überliefert.",
    sourceNote: null,
    createdAt: "2026-09-01T08:00:00.000Z",
    updatedAt: "2026-09-20T10:00:00.000Z",
    publishedAt: "2026-09-20T10:00:00.000Z",
  },
};

const ohneDatumDetail = {
  event: {
    ...ohneDatum,
    notes: null,
    sourceNote: "Pfarrarchiv Mining, Totenrotel von 1950.",
    createdAt: "2026-09-01T08:00:00.000Z",
    updatedAt: "2026-09-20T10:00:00.000Z",
    publishedAt: "2026-09-20T10:00:00.000Z",
  },
};

const konzertDetail = {
  event: {
    ...konzert,
    notes: "Für den Stehchoral sind die Noten mitzunehmen.",
    sourceNote: "Programmheft im Vereinsarchiv, Blatt 3.",
    createdAt: "2026-09-01T08:00:00.000Z",
    updatedAt: "2026-09-20T10:00:00.000Z",
    publishedAt: "2026-09-20T10:00:00.000Z",
  },
};

type AuftrittsArt = {
  id: string;
  kind: string;
  title: string;
  venue: string | null;
  dateYear: number | null;
  dateMonth: number | null;
  dateDay: number | null;
  dateApproximate: boolean;
  datePrecision: string;
  dateDisplay: string;
  startTime: string | null;
  published: boolean;
};

// Die Jahreszusammenfassung der Vertragssprache: absteigend, Auftritte
// ohne Datum zuletzt.
function jahreAusBestand(bestand: AuftrittsArt[]) {
  const zählung = new Map<number | null, number>();
  for (const eintrag of bestand) {
    zählung.set(eintrag.dateYear, (zählung.get(eintrag.dateYear) ?? 0) + 1);
  }
  return [...zählung.entries()]
    .sort((links, rechts) => {
      if (links[0] === null) return 1;
      if (rechts[0] === null) return -1;
      return rechts[0] - links[0];
    })
    .map(([year, count]) => ({ year, count }));
}

function json(body: unknown, status = 200) {
  return {
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  };
}

function problem(title: string, status: number) {
  return {
    status,
    contentType: "application/problem+json",
    body: JSON.stringify({ title }),
  };
}

// Der Abruf des Verzeichnisses mit und ohne Jahresparameter (die
// Vertragssuche.filternt über die Abfragezeichenkette).
const ereignisRoute = /\/api\/events(\?.*)?$/;

async function mockSitzung(page: Page, me: unknown) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}

test("Mitglied sieht Auftritte mit Jahresleiste und ehrlichen Daten", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const bestand: AuftrittsArt[] = [konzert, umJahr, ohneDatum];
  await page.route(ereignisRoute, (route) => {
    const adresse = new URL(route.request().url());
    const jahr = adresse.searchParams.get("year");
    const sichtbare = bestand.filter((eintrag) => eintrag.published);
    const events = jahr
      ? sichtbare.filter((eintrag) => eintrag.dateYear === Number(jahr))
      : sichtbare;
    return route.fulfill(
      json({
        years: jahreAusBestand(sichtbare),
        total: sichtbare.length,
        events,
      }),
    );
  });

  await page.goto("/auftritte/");
  await expect(
    page.getByRole("heading", { name: "Auftritte" }).first(),
  ).toBeVisible();
  // Die Jahresleiste trägt die echte Aufteilung des Bestands.
  const leiste = page.getByRole("navigation", { name: "Auftritte nach Jahr" });
  await expect(leiste.getByRole("button", { name: "Alle" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await expect(leiste.getByRole("button", { name: "1950 (2)" })).toBeVisible();
  await expect(
    leiste.getByRole("button", { name: "Ohne Jahr (1)" }),
  ).toBeVisible();

  const konzertEintrag = page
    .getByRole("listitem")
    .filter({ hasText: "Frühlingskonzert" });
  await expect(konzertEintrag.getByText("12. Mai 1950")).toBeVisible();
  await expect(
    konzertEintrag.getByText("Konzert", { exact: true }),
  ).toBeVisible();
  await expect(konzertEintrag.getByText("Stadtsaal Mining")).toBeVisible();
  await expect(konzertEintrag.getByRole("link")).toHaveAttribute(
    "href",
    `/auftritt/?id=${konzertId}`,
  );
  await expect(page.getByText("um 1950", { exact: true })).toBeVisible();
  await expect(
    page.getByText("Datum unbekannt", { exact: true }),
  ).toBeVisible();
  // Keine erfundene Genauigkeit: aus „um 1950" wird kein 1. Januar.
  await expect(page.getByText("1. Januar")).toHaveCount(0);
  await expect(page.getByText("Entwurf")).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Veröffentlichen" }),
  ).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Zurückziehen" })).toHaveCount(
    0,
  );
  await expect(
    page.getByRole("button", { name: "Bearbeiten", exact: true }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("heading", { name: "Neuer Auftritt" }),
  ).toHaveCount(0);

  // Auch im Handymaß bleibt die Jahresleiste ohne Seitenüberlauf.
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);

  expect(errors).toEqual([]);
});

test("Ein Jahr in der Leiste filtert die Auftritte; Ohne Jahr bleibt ehrlich", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const bestand: AuftrittsArt[] = [konzert, umJahr, ohneDatum];
  await page.route(ereignisRoute, (route) => {
    const adresse = new URL(route.request().url());
    const jahr = adresse.searchParams.get("year");
    const sichtbare = bestand.filter((eintrag) => eintrag.published);
    const events = jahr
      ? sichtbare.filter((eintrag) => eintrag.dateYear === Number(jahr))
      : sichtbare;
    return route.fulfill(
      json({
        years: jahreAusBestand(sichtbare),
        total: sichtbare.length,
        events,
      }),
    );
  });

  await page.goto("/auftritte/");
  const leiste = page.getByRole("navigation", { name: "Auftritte nach Jahr" });
  await leiste.getByRole("button", { name: "1950 (2)" }).click();
  await expect(page).toHaveURL(/\/auftritte\/\?jahr=1950$/);
  await expect(
    page.getByRole("listitem").filter({ hasText: "Frühlingskonzert" }),
  ).toBeVisible();
  await expect(
    page.getByRole("listitem").filter({ hasText: "Sängerfest" }),
  ).toBeVisible();
  await expect(
    page.getByRole("listitem").filter({ hasText: "Gedenkgottesdienst" }),
  ).toHaveCount(0);
  // Die Leiste bleibt die ganze Übersicht, auch im gefilterten Zustand.
  await expect(
    leiste.getByRole("button", { name: "Ohne Jahr (1)" }),
  ).toBeVisible();

  await leiste.getByRole("button", { name: "Ohne Jahr (1)" }).click();
  await expect(page).toHaveURL(/\/auftritte\/\?jahr=ohne$/);
  await expect(
    page.getByRole("listitem").filter({ hasText: "Gedenkgottesdienst" }),
  ).toBeVisible();
  await expect(
    page.getByRole("listitem").filter({ hasText: "Frühlingskonzert" }),
  ).toHaveCount(0);

  await leiste.getByRole("button", { name: "Alle" }).click();
  await expect(page).toHaveURL(/\/auftritte\/$/);
  await expect(
    page.getByRole("listitem").filter({ hasText: "Frühlingskonzert" }),
  ).toBeVisible();
  await expect(
    page.getByRole("listitem").filter({ hasText: "Gedenkgottesdienst" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Mitglied öffnet einen Auftritt per Direktlink; Entwürfe bleiben unfindbar", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${konzertId}`, (route) =>
    route.fulfill(json(konzertDetail)),
  );
  await page.route(`**/api/events/${entwurfId}`, (route) =>
    route.fulfill(problem("Der Auftritt wurde nicht gefunden.", 404)),
  );

  await page.goto(`/auftritt/?id=${konzertId}`);
  await expect(
    page.getByRole("heading", { name: "Frühlingskonzert" }),
  ).toBeVisible();
  await expect(page.getByText("Konzert", { exact: true })).toBeVisible();
  await expect(page.getByText("12. Mai 1950")).toBeVisible();
  await expect(page.getByText("Stadtsaal Mining")).toBeVisible();
  await expect(page.getByText("19:30 Uhr")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Hinweise" })).toBeVisible();
  await expect(
    page.getByText("Für den Stehchoral sind die Noten mitzunehmen."),
  ).toBeVisible();
  await expect(page.getByRole("heading", { name: "Quelle" })).toBeVisible();
  await expect(
    page.getByText("Programmheft im Vereinsarchiv, Blatt 3."),
  ).toBeVisible();
  // Nützliche leere Abschnitte für die folgenden Abschnitte.
  await expect(
    page.getByText("Das Programm wurde noch nicht erfasst."),
  ).toBeVisible();
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Dokumente hinterlegt."),
  ).toBeVisible();
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Aufnahmen hinterlegt."),
  ).toBeVisible();
  await expect(page.getByText("Datum unsicher")).toHaveCount(0);

  // Der Direktlink übersteht das Neuladen der Seite.
  await page.reload();
  await expect(
    page.getByRole("heading", { name: "Frühlingskonzert" }),
  ).toBeVisible();

  // Ein Entwurf bleibt für Mitglieder unfindbar.
  await page.goto(`/auftritt/?id=${entwurfId}`);
  await expect(
    page.getByText("Der Auftritt wurde nicht gefunden.", { exact: false }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Unsichere und unbekannte Daten bleiben im Lesesaal gekennzeichnet", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${umJahrId}`, (route) =>
    route.fulfill(json(umJahrDetail)),
  );
  await page.route(`**/api/events/${ohneDatumId}`, (route) =>
    route.fulfill(json(ohneDatumDetail)),
  );

  await page.goto(`/auftritt/?id=${umJahrId}`);
  await expect(page.getByRole("heading", { name: "Sängerfest" })).toBeVisible();
  await expect(page.getByText("um 1950")).toBeVisible();
  // Die Unsicherheitsmarke liest die Prüfung am Etikett selbst.
  await expect(page.locator(".datum-unsicher")).toHaveText("Datum unsicher");
  await expect(
    page.getByText("Das genaue Datum ist nicht überliefert."),
  ).toBeVisible();
  // Kein erfundenes Kalenderdatum für den ungefähren Jahresbericht.
  await expect(page.getByText("1. Januar")).toHaveCount(0);

  await page.goto(`/auftritt/?id=${ohneDatumId}`);
  await expect(
    page.getByRole("heading", { name: "Gedenkgottesdienst" }),
  ).toBeVisible();
  await expect(page.locator(".auftritt-datum")).toContainText(
    "Datum unbekannt",
  );
  await expect(page.locator(".datum-unsicher")).toHaveText("Datum unsicher");
  await expect(
    page.getByText("Pfarrarchiv Mining, Totenrotel von 1950."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Redaktion sieht den Entwurf, veröffentlicht ihn und kann ihn zurückziehen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  let bestand: AuftrittsArt[] = [konzert, umJahr, ohneDatum, entwurf];
  await page.route(ereignisRoute, (route) => {
    const adresse = new URL(route.request().url());
    const jahr = adresse.searchParams.get("year");
    const sichtbar = bestand;
    const events = jahr
      ? sichtbar.filter((eintrag) => eintrag.dateYear === Number(jahr))
      : sichtbar;
    return route.fulfill(
      json({
        years: jahreAusBestand(sichtbar),
        total: sichtbar.length,
        events,
      }),
    );
  });
  const veröffentlichungen: string[] = [];
  await page.route(`**/api/events/${entwurfId}/publish`, (route) => {
    veröffentlichungen.push(route.request().url());
    bestand = bestand.map((eintrag) =>
      eintrag.id === entwurfId ? { ...eintrag, published: true } : eintrag,
    );
    return route.fulfill(
      json({ event: bestand.find((eintrag) => eintrag.id === entwurfId) }),
    );
  });

  await page.goto("/auftritte/");
  await expect(
    page.getByRole("heading", { name: "Neuer Auftritt" }),
  ).toBeVisible();
  const zeile = page
    .getByRole("listitem")
    .filter({ hasText: "Hochzeit der Familie Berger" });
  await expect(zeile.getByText("Entwurf")).toBeVisible();
  await expect(page.getByRole("button", { name: "1951 (1)" })).toBeVisible();

  await zeile.getByRole("button", { name: "Veröffentlichen" }).click();
  await expect(
    page.getByText("Auftritt veröffentlicht. Mitglieder sehen ihn ab sofort."),
  ).toBeVisible();
  expect(veröffentlichungen).toHaveLength(1);
  expect(veröffentlichungen[0]).toContain(`/api/events/${entwurfId}/publish`);
  await expect(zeile.getByText("Entwurf")).toHaveCount(0);
  await expect(zeile.getByText("Veröffentlicht")).toBeVisible();
  await expect(
    zeile.getByRole("button", { name: "Zurückziehen" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Redaktion legt einen Auftritt mit ehrlichem Datum an", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  let bestand: AuftrittsArt[] = [konzert, umJahr, ohneDatum];
  const anfragen: Record<string, unknown>[] = [];
  await page.route(ereignisRoute, (route) => {
    const adresse = new URL(route.request().url());
    const jahr = adresse.searchParams.get("year");
    if (route.request().method() === "POST") {
      anfragen.push(route.request().postDataJSON() as Record<string, unknown>);
      const neu: AuftrittsArt = {
        id: "00000000-0000-0000-0000-00000000e005",
        kind: "concert",
        title: "Serenade am Stadtplatz",
        venue: null,
        dateYear: 1950,
        dateMonth: 7,
        dateDay: null,
        dateApproximate: true,
        datePrecision: "month",
        dateDisplay: "um Juli 1950",
        startTime: null,
        published: false,
      };
      bestand = [...bestand, neu];
      return route.fulfill(json({ event: neu }, 201));
    }
    const sichtbare = bestand;
    const events = jahr
      ? sichtbare.filter((eintrag) => eintrag.dateYear === Number(jahr))
      : sichtbare;
    return route.fulfill(
      json({
        years: jahreAusBestand(sichtbare),
        total: sichtbare.length,
        events,
      }),
    );
  });

  await page.goto("/auftritte/");
  const anlegen = page.locator(".auftritt-anlegen");
  await anlegen
    .getByLabel("Titel", { exact: true })
    .fill("Serenade am Stadtplatz");
  await anlegen.getByLabel("Jahr").fill("1950");
  await anlegen.getByLabel("Monat").selectOption({ label: "Juli" });
  await anlegen.getByLabel("Tag").fill("");
  await anlegen.getByRole("checkbox", { name: "Datum unsicher" }).check();
  await anlegen.getByRole("button", { name: "Auftritt anlegen" }).click();
  await expect(page.getByText("Auftritt angelegt.")).toBeVisible();

  expect(anfragen).toHaveLength(1);
  expect(anfragen[0]).toEqual({
    kind: "concert",
    title: "Serenade am Stadtplatz",
    venue: null,
    dateYear: 1950,
    dateMonth: 7,
    dateDay: null,
    dateApproximate: true,
    startTime: null,
    notes: null,
    sourceNote: null,
  });
  const zeile = page
    .getByRole("listitem")
    .filter({ hasText: "Serenade am Stadtplatz" });
  await expect(zeile.getByText("Entwurf")).toBeVisible();
  await expect(zeile.getByText("um Juli 1950")).toBeVisible();

  expect(errors).toEqual([]);
});

test("Redaktion speichert einen Auftritt mit unbekanntem Datum", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  const neueId = "00000000-0000-0000-0000-00000000e005";
  let bestand: AuftrittsArt[] = [konzert, umJahr, ohneDatum];
  const anfragen: Record<string, unknown>[] = [];
  await page.route(ereignisRoute, (route) => {
    const adresse = new URL(route.request().url());
    const jahr = adresse.searchParams.get("year");
    if (route.request().method() === "POST") {
      anfragen.push(route.request().postDataJSON() as Record<string, unknown>);
      const neu: AuftrittsArt = {
        ...ohneDatum,
        id: neueId,
        kind: "concert",
        title: "Gedenkandacht",
        venue: null,
      };
      bestand = [...bestand, neu];
      return route.fulfill(json({ event: neu }, 201));
    }
    const sichtbare = bestand;
    const events = jahr
      ? sichtbare.filter((eintrag) => eintrag.dateYear === Number(jahr))
      : sichtbare;
    return route.fulfill(
      json({
        years: jahreAusBestand(sichtbare),
        total: sichtbare.length,
        events,
      }),
    );
  });
  await page.route(`**/api/events/${neueId}`, (route) => {
    anfragen.push(route.request().postDataJSON() as Record<string, unknown>);
    bestand = bestand.map((eintrag) =>
      eintrag.id === neueId
        ? { ...eintrag, title: "Gedenkandacht am Friedhof" }
        : eintrag,
    );
    return route.fulfill(
      json({ event: bestand.find((eintrag) => eintrag.id === neueId) }),
    );
  });

  await page.goto("/auftritte/");
  const anlegen = page.locator(".auftritt-anlegen");
  await anlegen.getByLabel("Titel", { exact: true }).fill("Gedenkandacht");
  // Jahr, Monat und Tag bleiben leer: nichts ist überliefert, und das
  // Formular erfindet kein Kalenderdatum.
  await anlegen.getByRole("button", { name: "Auftritt anlegen" }).click();
  await expect(page.getByText("Auftritt angelegt.")).toBeVisible();

  expect(anfragen).toHaveLength(1);
  expect(anfragen[0]).toEqual({
    kind: "concert",
    title: "Gedenkandacht",
    venue: null,
    dateYear: null,
    dateMonth: null,
    dateDay: null,
    dateApproximate: false,
    startTime: null,
    notes: null,
    sourceNote: null,
  });
  const zeile = page.getByRole("listitem").filter({ hasText: "Gedenkandacht" });
  await expect(zeile.getByText("Datum unbekannt")).toBeVisible();

  // Auch die Bearbeitung trägt das Datum als ganzen Block: ein
  // Tippfehler im Titel erzwingt kein erfundenes Jahr.
  await zeile.getByRole("button", { name: "Bearbeiten" }).click();
  const bearbeiten = page.locator(".auftritt-bearbeiten");
  await bearbeiten
    .getByLabel("Titel", { exact: true })
    .fill("Gedenkandacht am Friedhof");
  await bearbeiten
    .getByRole("button", { name: "Änderungen speichern" })
    .click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();

  expect(anfragen).toHaveLength(2);
  expect(anfragen[1]).toEqual({
    kind: "concert",
    title: "Gedenkandacht am Friedhof",
    venue: "",
    startTime: "",
    date: { year: null, month: null, day: null, approximate: false },
  });
  const korrigiert = page
    .getByRole("listitem")
    .filter({ hasText: "Gedenkandacht am Friedhof" });
  await expect(korrigiert.getByText("Datum unbekannt")).toBeVisible();

  expect(errors).toEqual([]);
});

test("Ein gestörter Abruf erklärt sich und bietet den erneuten Versuch an", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(ereignisRoute, (route) =>
    route.fulfill(
      problem(
        "Der Auftrittsverzeichnis-Dienst ist derzeit nicht verfügbar.",
        503,
      ),
    ),
  );

  await page.goto("/auftritte/");
  await expect(
    page.getByText("Das Archiv antwortet nicht. Bitte erneut versuchen."),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toBeVisible();

  // Der gestörte Abruf bleibt registriert; der spätere Erfolg tritt an
  // seine Stelle (die zuletzt angemeldete Route gewinnt).
  await page.route(ereignisRoute, (route) =>
    route.fulfill(
      json({
        years: [{ year: 1950, count: 1 }],
        total: 1,
        events: [konzert],
      }),
    ),
  );
  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(
    page.getByRole("listitem").filter({ hasText: "Frühlingskonzert" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});
