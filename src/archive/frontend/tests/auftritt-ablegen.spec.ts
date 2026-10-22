import { expect, type Page, test } from "@playwright/test";
import { mockLeereAufnahmen } from "./aufnahmen-mock";
import {
  editorMe,
  json,
  liste,
  mockEntwuerfe,
  mockSitzung,
  mondEintrag,
  mondId,
  problem,
} from "./lieder-mock";

// #131: "Neuer Auftritt" durch Ablegen eines Programmzettels oder Konzertvideos.
// Das Lesen und die Zuordnung zum Liederverzeichnis sind der Server-Antwort
// nachgebildet (POST /api/auftritt-lesungen; die echte Zuordnung und die
// Prüfungen dahinter stehen in AuftrittLesenApiTests.cs und im Auswertungssatz
// ProgrammLesenEvaluationTests.cs). Diese Tests beweisen, was der Browser
// daraus macht: Felder, Vorrang der Handarbeit, Zuordnung, Speichern, Angefangen.

const auftrittId = "00000000-0000-0000-0000-0000000e0001";
const aveId = "00000000-0000-0000-0000-00000000a010";
const waldId = "00000000-0000-0000-0000-00000000a030";
const aufnahmeId = "00000000-0000-0000-0000-0000000f0001";

const foto = (name = "programm.jpg") => ({
  name,
  mimeType: "image/jpeg",
  buffer: Buffer.from("foto"),
});
const video = (name = "konzert.mp4") => ({
  name,
  mimeType: "video/mp4",
  buffer: Buffer.from("video"),
});

type Anfrage = { entwurfId: string; anfrageId: number };

const programmLesung = (a: Anfrage) => ({
  ...a,
  felder: [
    { feld: "title", wert: "Frühjahrskonzert", sicher: true },
    { feld: "kind", wert: "concert", sicher: true },
    { feld: "jahr", wert: "1951", sicher: true },
    { feld: "monat", wert: "5", sicher: false },
    { feld: "tag", wert: "12", sicher: true },
    { feld: "venue", wert: "Gasthaus zur Post", sicher: true },
  ],
  programm: [
    {
      text: "Ave Maria",
      sicher: true,
      lied: { id: aveId, titel: "Ave Maria", sicher: true },
      kandidaten: [],
    },
    {
      text: "Der Mond ist aufgegangn",
      sicher: true,
      lied: { id: mondId, titel: mondEintrag.title, sicher: false },
      kandidaten: [{ id: mondId, titel: mondEintrag.title }],
    },
    { text: "Waldfahrt", sicher: true, lied: null, kandidaten: [] },
  ],
});

type Umgebung = {
  lesungen: Anfrage[];
  angelegt: Record<string, unknown>[];
  programme: Record<string, unknown>[];
  neueLieder: Record<string, unknown>[];
  sitzungen: Record<string, unknown>[];
  aufnahmen: Record<string, unknown>[];
  angehaengt: Record<string, unknown>[];
  entwuerfe: Map<string, Record<string, unknown>>;
  verworfen: string[];
};

async function mockUmgebung(
  page: Page,
  lesung: (a: Anfrage) => { status: number; body: unknown } = (a) => ({
    status: 200,
    body: programmLesung(a),
  }),
  freigabe?: Promise<void>,
): Promise<Umgebung> {
  const umgebung: Umgebung = {
    lesungen: [],
    angelegt: [],
    programme: [],
    neueLieder: [],
    sitzungen: [],
    aufnahmen: [],
    angehaengt: [],
    entwuerfe: new Map(),
    verworfen: [],
  };
  await mockSitzung(page, editorMe);
  await mockEntwuerfe(page);
  await mockLeereAufnahmen(page);
  await page.route("**/api/admin/members", (route) =>
    route.fulfill(json({ members: [] })),
  );
  await page.route(/\/api\/events(\?.*)?$/, (route) => {
    if (route.request().method() !== "POST") {
      return route.fulfill(json({ years: [], total: 0, events: [] }));
    }
    const body = route.request().postDataJSON() as Record<string, unknown>;
    umgebung.angelegt.push(body);
    return route.fulfill(
      json(
        {
          event: {
            id: auftrittId,
            kind: body.kind,
            title: body.title,
            venue: body.venue,
            dateYear: body.dateYear,
            dateMonth: body.dateMonth,
            dateDay: body.dateDay,
            dateApproximate: body.dateApproximate,
            datePrecision: "day",
            dateDisplay: "12. Mai 1951",
            startTime: null,
            notes: null,
            sourceNote: null,
            documents: [],
            createdAt: "2026-10-21T10:00:00.000Z",
            updatedAt: "2026-10-21T10:00:00.000Z",
          },
        },
        201,
      ),
    );
  });
  await page.route(`**/api/events/${auftrittId}/programme/items`, (route) => {
    umgebung.programme.push(
      route.request().postDataJSON() as Record<string, unknown>,
    );
    return route.fulfill(
      json({ programme: { id: "p1", rowVersion: 1, items: [] } }),
    );
  });
  await page.route("**/api/lieder**", (route) => {
    if (route.request().method() === "POST") {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      umgebung.neueLieder.push(body);
      return route.fulfill(
        json(
          {
            lied: {
              ...mondEintrag,
              id: waldId,
              title: body.title,
              dateien: [],
              rowVersion: 1,
              lyrics: null,
              createdAt: "2026-10-21T10:00:00.000Z",
              updatedAt: "2026-10-21T10:00:00.000Z",
            },
          },
          201,
        ),
      );
    }
    return route.fulfill(json(liste([mondEintrag])));
  });
  await page.route("**/api/auftritt-lesungen", async (route) => {
    const text = route.request().postDataBuffer()?.toString("latin1") ?? "";
    const wert = (name: string) =>
      new RegExp(`name="${name}"\\r\\n\\r\\n([^\\r]*)`).exec(text)?.[1] ?? "";
    const anfrage = {
      entwurfId: wert("entwurfId"),
      anfrageId: Number(wert("anfrageId")),
    };
    umgebung.lesungen.push(anfrage);
    await freigabe;
    const { status, body } = lesung(anfrage);
    return route.fulfill(
      status === 200
        ? json(body)
        : problem(String((body as { title: string }).title), status),
    );
  });
  await page.route("**/api/auftritt-entwuerfe", (route) =>
    route.fulfill(
      json({
        entwuerfe: [...umgebung.entwuerfe.entries()].map(([id, e]) => ({
          entwurfId: id,
          titel: e.titel,
          dateien: e.dateien,
          aktualisiertAm: "2026-10-21T09:00:00.000Z",
        })),
      }),
    ),
  );
  await page.route("**/api/auftritt-entwuerfe/*", (route) => {
    const id = route.request().url().split("/").pop() ?? "";
    switch (route.request().method()) {
      case "PUT":
        umgebung.entwuerfe.set(
          id,
          route.request().postDataJSON() as Record<string, unknown>,
        );
        return route.fulfill(
          json({ aktualisiertAm: "2026-10-21T09:00:00.000Z" }),
        );
      case "DELETE":
        umgebung.entwuerfe.delete(id);
        umgebung.verworfen.push(id);
        return route.fulfill({ status: 204 });
      default: {
        const entwurf = umgebung.entwuerfe.get(id);
        return entwurf
          ? route.fulfill(
              json({
                entwurf: {
                  ...entwurf,
                  aktualisiertAm: "2026-10-21T09:00:00.000Z",
                },
              }),
            )
          : route.fulfill(problem("Nicht gefunden.", 404));
      }
    }
  });
  // Die Übertragung der Dateien des Auftritts (Upload-Sitzung, Speicher, Abschluss).
  await page.route(
    `**/api/events/${auftrittId}/dateien/upload-session`,
    (route) => {
      umgebung.sitzungen.push(
        route.request().postDataJSON() as Record<string, unknown>,
      );
      return route.fulfill(
        json(
          {
            uploadSessionId: `sitz-${umgebung.sitzungen.length}`,
            blobName: null,
            uploadUrl: "https://speicher.test/ubertragung?sig=1",
            expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(),
            maxBytes: 10737418240,
            blockBytes: 5242880,
          },
          201,
        ),
      );
    },
  );
  await page.route(/speicher\.test\/ubertragung/, (route) =>
    route.fulfill(json({}, 201)),
  );
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(
      json({
        datei: {
          id: `00000000-0000-0000-0000-00000000d00${umgebung.sitzungen.length}`,
          kind: "sonstiges",
          contentType: "image/jpeg",
          sizeBytes: 4,
          fileName: "programm.jpg",
          createdAt: "2026-10-21T10:00:00.000Z",
        },
      }),
    ),
  );
  await page.route(`**/api/events/${auftrittId}/recordings`, (route) => {
    if (route.request().method() !== "POST") {
      return route.fulfill(json({ eventId: auftrittId, recordings: [] }));
    }
    const body = route.request().postDataJSON() as Record<string, unknown>;
    umgebung.aufnahmen.push(body);
    return route.fulfill(
      json(
        {
          recording: {
            id: aufnahmeId,
            eventId: auftrittId,
            label: body.label,
            kind: body.kind,
            isPublished: false,
            downloadEnabled: false,
            durationSeconds: null,
            playback: {
              state: "missing",
              source: null,
              dateiId: null,
              contentType: null,
              sizeBytes: null,
            },
            createdAt: "2026-10-21T10:00:00.000Z",
            editor: {
              version: 0,
              canChangeFiles: true,
              publishedAt: null,
              original: { file: null },
              playbackCopy: { file: null },
            },
          },
        },
        201,
      ),
    );
  });
  await page.route(
    `**/api/recordings/${aufnahmeId}/dateien/original`,
    (route) => {
      umgebung.angehaengt.push(
        route.request().postDataJSON() as Record<string, unknown>,
      );
      return route.fulfill(json({ recording: { id: aufnahmeId } }));
    },
  );
  await page.route(`**/api/recordings/${aufnahmeId}`, (route) =>
    route.fulfill(json({ recording: { id: aufnahmeId } })),
  );
  return umgebung;
}

async function ablageOeffnen(page: Page, adresse = "/auftritte/") {
  await page.goto(adresse);
  if (!adresse.includes("entwurf=")) {
    await page.locator("details.auftritt-anlegen > summary").click();
  }
}

const anlegen = (page: Page) => page.locator("details.auftritt-anlegen");

const dateienWaehlen = (page: Page) =>
  page.getByLabel("Dateien für den Auftritt auswählen");

test("ein Foto des Programms füllt den Auftritt und ordnet das Programm zu, Speichern legt alles an", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const umgebung = await mockUmgebung(page);

  await ablageOeffnen(page);
  await dateienWaehlen(page).setInputFiles(foto());

  await expect(page.getByLabel("Titel", { exact: true })).toHaveValue(
    "Frühjahrskonzert",
  );
  await expect(anlegen(page).getByLabel("Jahr", { exact: true })).toHaveValue(
    "1951",
  );
  await expect(anlegen(page).getByLabel("Monat", { exact: true })).toHaveValue(
    "5",
  );
  await expect(anlegen(page).getByLabel("Tag", { exact: true })).toHaveValue(
    "12",
  );
  await expect(page.getByLabel("Ort (optional)")).toHaveValue(
    "Gasthaus zur Post",
  );
  // Der unsichere Monat ist als solcher markiert, die anderen Felder als Vorschlag.
  await expect(
    page.locator('.lied-ablegen [data-vorschlag="unsicher"]'),
  ).toHaveCount(1);
  await expect(
    page.locator('.lied-ablegen [data-vorschlag="sicher"]'),
  ).toHaveCount(5);

  // Das Programm: ein sicherer Treffer, ein unsicherer Vorschlag, ein offener Punkt.
  await expect(page.locator('li[data-lied="sicher"]')).toHaveCount(1);
  await expect(page.locator('li[data-lied="unsicher"]')).toContainText(
    "Vorschlag, unsicher: Der Mond ist aufgegangen",
  );
  await expect(page.locator('li[data-lied="offen"]')).toContainText(
    "Noch kein Lied zugeordnet.",
  );
  const speichern = page.getByRole("button", { name: "Speichern" });
  await expect(speichern).toBeDisabled();
  await expect(page.locator("[data-fehlt]")).toContainText(
    "ein Lied für den offenen Programmpunkt",
  );

  await page
    .getByRole("button", { name: "Der Mond ist aufgegangn übernehmen" })
    .click();
  await page
    .getByRole("button", { name: "Waldfahrt als neues Lied anlegen" })
    .click();
  await expect(page.locator('li[data-lied="offen"]')).toHaveCount(0);
  expect(umgebung.neueLieder).toHaveLength(1);
  expect(umgebung.neueLieder[0].title).toBe("Waldfahrt");

  await expect(speichern).toBeEnabled();
  await speichern.click();
  await expect(page).toHaveURL(new RegExp(`/auftritt/\\?id=${auftrittId}`));

  // Ein Auftritt, sein Programm in der Reihenfolge des Zettels und der Zettel als Dokument.
  expect(umgebung.angelegt).toEqual([
    {
      kind: "concert",
      title: "Frühjahrskonzert",
      venue: "Gasthaus zur Post",
      dateYear: 1951,
      dateMonth: 5,
      dateDay: 12,
      dateApproximate: false,
    },
  ]);
  expect(umgebung.programme).toEqual([
    { items: [{ liedId: aveId }, { liedId: mondId }, { liedId: waldId }] },
  ]);
  expect(umgebung.sitzungen).toHaveLength(1);
  expect(umgebung.sitzungen[0]).toMatchObject({
    kind: "sonstiges",
    contentType: "image/jpeg",
    fileName: "programm.jpg",
  });
  // Mit dem Speichern endet der Entwurf.
  await expect.poll(() => umgebung.verworfen.length).toBe(1);
  expect(errors).toEqual([]);
});

test("was die Redaktion getippt hat, bleibt, auch wenn die Lesung später ankommt", async ({
  page,
}) => {
  let frei: () => void = () => {};
  const warten = new Promise<void>((weiter) => {
    frei = weiter;
  });
  const umgebung = await mockUmgebung(page, undefined, warten);

  await ablageOeffnen(page);
  await dateienWaehlen(page).setInputFiles(foto());
  await expect(page.getByText("Wird gelesen …")).toBeVisible();
  await page.getByLabel("Titel", { exact: true }).fill("Mein Titel");
  await page.getByLabel("Ort (optional)").fill("Irgendwo");
  await page.getByLabel("Ort (optional)").fill("");
  frei();

  await expect(page.getByText("Gelesen", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Titel", { exact: true })).toHaveValue(
    "Mein Titel",
  );
  // Auch ein von Hand geleertes Feld wird nicht überschrieben.
  await expect(page.getByLabel("Ort (optional)")).toHaveValue("");
  await expect(anlegen(page).getByLabel("Jahr", { exact: true })).toHaveValue(
    "1951",
  );
  expect(umgebung.lesungen).toHaveLength(1);
});

test("ein Konzertvideo wird die Aufnahme des Auftritts, ohne Lesen", async ({
  page,
}) => {
  const umgebung = await mockUmgebung(page);

  await ablageOeffnen(page);
  await dateienWaehlen(page).setInputFiles(video());
  await expect(page.getByLabel("Art von konzert.mp4")).toHaveValue("aufnahme");
  await page.getByLabel("Titel", { exact: true }).fill("Sommerkonzert");
  await page.getByRole("button", { name: "Speichern" }).click();
  await expect(page).toHaveURL(new RegExp(`/auftritt/\\?id=${auftrittId}`));

  expect(umgebung.lesungen).toHaveLength(0);
  expect(umgebung.aufnahmen).toEqual([{ label: "konzert", kind: "video" }]);
  expect(umgebung.sitzungen[0]).toMatchObject({
    kind: "aufnahme",
    contentType: "video/mp4",
  });
  expect(umgebung.angehaengt).toHaveLength(1);
  // Ohne Programm gibt es keinen Aufruf an das Programm.
  expect(umgebung.programme).toHaveLength(0);
});

test("ist die KI nicht verfügbar, bleibt das Formular von Hand ausfüllbar", async ({
  page,
}) => {
  const umgebung = await mockUmgebung(page, () => ({
    status: 429,
    body: {
      title:
        "Das Monatsbudget der KI ist erreicht. Das Formular lässt sich von Hand ausfüllen.",
    },
  }));

  await ablageOeffnen(page);
  await dateienWaehlen(page).setInputFiles(foto());
  await expect(page.locator('[data-ki="nicht-verfuegbar"]')).toContainText(
    "Die KI steht nicht zur Verfügung",
  );
  await page.getByLabel("Titel", { exact: true }).fill("Konzert");
  await page.getByRole("button", { name: "Speichern" }).click();
  await expect(page).toHaveURL(new RegExp(`/auftritt/\\?id=${auftrittId}`));
  expect(umgebung.angelegt[0].title).toBe("Konzert");
});

test("ein Lesefehler lässt sich wiederholen", async ({ page }) => {
  let versuch = 0;
  const umgebung = await mockUmgebung(page, (a) => {
    versuch++;
    return versuch === 1
      ? {
          status: 502,
          body: { title: "Das Lesen ist gescheitert. Bitte erneut versuchen." },
        }
      : { status: 200, body: programmLesung(a) };
  });

  await ablageOeffnen(page);
  await dateienWaehlen(page).setInputFiles(foto());
  await expect(page.locator(".lied-ablegen").getByRole("alert")).toContainText(
    "Das Lesen ist gescheitert",
  );
  await page.getByRole("button", { name: "programm.jpg lesen" }).click();
  await expect(page.getByLabel("Titel", { exact: true })).toHaveValue(
    "Frühjahrskonzert",
  );
  expect(umgebung.lesungen.map((a) => a.anfrageId)).toEqual([1, 2]);
});

test("ohne Datei bleibt der kleine Link zum Formular von Hand", async ({
  page,
}) => {
  await mockUmgebung(page);
  await ablageOeffnen(page);
  await page.getByRole("button", { name: "ohne Datei anlegen" }).click();
  await expect(
    page.getByRole("button", { name: "Auftritt anlegen" }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Stattdessen Dateien ablegen" })
    .click();
  await expect(dateienWaehlen(page)).toBeAttached();
});

test("Angefangen: der Entwurf kommt zurück, die Datei muss neu gewählt werden, Verwerfen löscht ihn", async ({
  page,
}) => {
  const umgebung = await mockUmgebung(page);
  await ablageOeffnen(page);
  await dateienWaehlen(page).setInputFiles(foto());
  await expect(page.getByLabel("Titel", { exact: true })).toHaveValue(
    "Frühjahrskonzert",
  );
  await page
    .getByRole("button", { name: "Der Mond ist aufgegangn übernehmen" })
    .click();
  await expect.poll(() => umgebung.entwuerfe.size).toBe(1);
  const [entwurfId] = [...umgebung.entwuerfe.keys()];
  // Der Entwurf hält das Formular und den Namen der Datei, nicht ihre Bytes.
  await expect
    .poll(() => JSON.stringify(umgebung.entwuerfe.get(entwurfId)))
    .toContain('"sicher":true');
  expect(JSON.stringify(umgebung.entwuerfe.get(entwurfId))).not.toContain(
    "Zm90bw",
  );

  // In der Verwaltung steht er unter "Angefangen".
  await page.goto("/verwaltung/");
  await expect(
    page.getByRole("link", { name: "Frühjahrskonzert fortsetzen" }),
  ).toBeVisible();

  // Fortsetzen stellt das Formular wieder her; die Datei fehlt noch.
  await page.getByRole("link", { name: "Frühjahrskonzert fortsetzen" }).click();
  await expect(page).toHaveURL(new RegExp(`entwurf=${entwurfId}`));
  await expect(page.getByLabel("Titel", { exact: true })).toHaveValue(
    "Frühjahrskonzert",
  );
  await expect(anlegen(page).getByLabel("Jahr", { exact: true })).toHaveValue(
    "1951",
  );
  await expect(page.locator('li[data-lied="offen"]')).toContainText(
    "Waldfahrt",
  );
  await expect(
    page.getByText("Die Datei liegt nicht mehr im Browser"),
  ).toBeVisible();
  await expect(page.locator("[data-fehlt]")).toContainText(
    "programm.jpg noch einmal abgelegt",
  );
  await page
    .getByRole("button", { name: "Waldfahrt als neues Lied anlegen" })
    .click();
  await expect(page.locator('li[data-lied="offen"]')).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Speichern" })).toBeDisabled();

  // Die Datei neu zu wählen macht Speichern möglich; gelesen wird nicht noch einmal.
  const lesungenVorher = umgebung.lesungen.length;
  await dateienWaehlen(page).setInputFiles(foto());
  await expect(
    page.getByText("Die Datei liegt nicht mehr im Browser"),
  ).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Speichern" })).toBeEnabled();
  expect(umgebung.lesungen).toHaveLength(lesungenVorher);

  await page.getByRole("button", { name: "Entwurf verwerfen" }).click();
  await expect.poll(() => umgebung.verworfen).toContain(entwurfId);
  await expect(page).toHaveURL(/\/auftritte\/$/);
});

test("Angefangen in der Verwaltung lässt einen Auftritt-Entwurf verwerfen", async ({
  page,
}) => {
  const umgebung = await mockUmgebung(page);
  umgebung.entwuerfe.set("00000000-0000-0000-0000-0000000c0001", {
    titel: "Sängerfest",
    dateien: 2,
  });
  await page.goto("/verwaltung/");
  await expect(page.getByText("Auftritt, 2 Dateien")).toBeVisible();
  await page.getByRole("button", { name: "Sängerfest verwerfen" }).click();
  await expect(
    page.getByRole("button", { name: "Sängerfest verwerfen" }),
  ).toHaveCount(0);
  expect(umgebung.verworfen).toEqual(["00000000-0000-0000-0000-0000000c0001"]);
});
