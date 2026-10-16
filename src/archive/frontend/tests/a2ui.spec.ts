import { expect, type Page, test } from "@playwright/test";
import {
  antwortStrom,
  json,
  liedDetailAntwort,
  mockSitzung,
  oberflaeche,
} from "./chat-mock";

// ARC-022-4: Antworten des Chats zeigen Karten aus dem Katalog des Archivs.
// Der Browser rendert nur, was der Server geprüft schickt und was dem Katalog
// ganz entspricht; ein falscher Client sendet hier Kaputtes und Fremdes.

const threadId = "00000000-0000-0000-0000-00000000c0de";
const liedId = "00000000-0000-0000-0000-0000000002a1";
const auftrittId = "00000000-0000-0000-0000-0000000003b1";
const titel = "Das Wandern ist des Müllers Lust";

const liedKarte = {
  component: "LiedKarte",
  liedId,
  titel,
  komponist: "Carl Friedrich Zöllner",
  textdichter: "Wilhelm Müller",
  textanfang: "Das Wandern ist des Müllers Lust, / das Wandern!",
};

const frage = "Zeige mir das Wandernlied als Karte";

async function mockArchiv(page: Page) {
  await mockSitzung(page);
  await page.route(`**/api/songs/${liedId}`, (route) =>
    route.fulfill(
      json(
        liedDetailAntwort({
          id: liedId,
          title: titel,
          composer: "Carl Friedrich Zöllner",
          lyricist: "Wilhelm Müller",
        }),
      ),
    ),
  );
}

function mockLauf(page: Page, body: string) {
  return page.route(/\/api\/chat$/, (route) =>
    route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body,
    }),
  );
}

async function stelleFrage(page: Page, text = frage) {
  await page.goto("/fragen/");
  await page.getByLabel("Frage stellen").fill(text);
  await page.getByRole("button", { name: "Absenden" }).click();
}

test("Eine Liedkarte erscheint zur Antwort, führt ins Archiv und bleibt nach dem Neuladen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockArchiv(page);
  await mockLauf(
    page,
    antwortStrom(threadId, [`Hier ist das Lied. [Quelle: ${titel}]`], {
      oberflaechen: oberflaeche(liedKarte),
      quellen: [{ id: liedId, label: titel }],
    }),
  );
  // So antwortet der Server beim Laden: Frage, Antwort und die erneut
  // geprüfte Karte.
  await page.route(/\/api\/chat\/thread\/[0-9a-f-]+$/, (route) =>
    route.fulfill(
      json({
        threadId,
        messages: [
          { role: "user", content: frage },
          {
            role: "assistant",
            content: `Hier ist das Lied. [Quelle: ${titel}]`,
            a2ui: oberflaeche(liedKarte),
          },
        ],
      }),
    ),
  );

  await stelleFrage(page);

  const karte = page.getByRole("article", { name: `Lied: ${titel}` });
  await expect(karte).toBeVisible();
  await expect(karte.getByRole("heading", { name: titel })).toBeVisible();
  await expect(karte.getByText("Carl Friedrich Zöllner")).toBeVisible();
  await expect(karte.getByText("Wilhelm Müller")).toBeVisible();
  await expect(
    karte.getByText("Das Wandern ist des Müllers Lust, /"),
  ).toBeVisible();
  const verweis = karte.getByRole("link", {
    name: `Lied im Archiv öffnen: ${titel}`,
  });
  // Der Verweis stammt aus der ID, nicht aus Text des Servers oder Modells.
  await expect(verweis).toHaveAttribute("href", `/lied/?id=${liedId}`);
  // Die Quellenverweise des Servers bleiben daneben bestehen.
  await expect(
    page.getByRole("link", { name: `Quelle: ${titel}` }),
  ).toBeVisible();

  // Nach dem Neuladen holt der Browser den Verlauf und zeigt die Karte wieder.
  await page.reload();
  await expect(
    page.getByRole("article", { name: `Lied: ${titel}` }),
  ).toBeVisible();
  await expect(
    page
      .getByRole("article", { name: `Lied: ${titel}` })
      .getByText("Carl Friedrich Zöllner"),
  ).toBeVisible();

  // Der Klick ruft keine Chat-Schnittstelle auf, sondern öffnet die Seite des
  // Archivs, die ihre Daten selbst von /api/songs holt.
  const chatAnfragen: string[] = [];
  page.on("request", (anfrage) => {
    if (/\/api\/chat$/.test(anfrage.url())) chatAnfragen.push(anfrage.url());
  });
  await page
    .getByRole("link", { name: `Lied im Archiv öffnen: ${titel}` })
    .click();
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${liedId}`));
  await expect(
    page.getByRole("heading", { name: titel }).first(),
  ).toBeVisible();
  expect(chatAnfragen).toEqual([]);
  expect(errors).toEqual([]);
});

test("Nach einer Entscheidung des Servers ohne Karte bleibt nur der Text", async ({
  page,
}) => {
  // Das Lied wurde inzwischen zurückgezogen: der Server schickt beim Laden
  // keine Karte mehr, und der Browser erfindet keine.
  await mockArchiv(page);
  await page.addInitScript(
    ([id]) => window.localStorage.setItem("arc-chat-thread", id),
    [threadId],
  );
  await page.route(/\/api\/chat\/thread\/[0-9a-f-]+$/, (route) =>
    route.fulfill(
      json({
        threadId,
        messages: [
          { role: "user", content: frage },
          { role: "assistant", content: "Das Lied war verzeichnet." },
        ],
      }),
    ),
  );

  await page.goto("/fragen/");

  await expect(page.getByText("Das Lied war verzeichnet.")).toBeVisible();
  await expect(page.getByRole("article", { name: /^Lied:/ })).toHaveCount(0);
});

const kaputteOberflaechen: Array<{ fall: string; nachrichten: unknown }> = [
  {
    fall: "unbekannte Komponente Button",
    nachrichten: oberflaeche({
      component: "Button",
      text: "Jetzt klicken",
      action: { event: { name: "boese" } },
    }),
  },
  {
    fall: "Komponente des Basis-Katalogs",
    nachrichten: oberflaeche({ component: "Text", text: "Basis-Text" }),
  },
  {
    fall: "zusätzliche Eigenschaft mit Adresse",
    nachrichten: oberflaeche({
      ...liedKarte,
      href: "https://boese.example/lied",
    }),
  },
  {
    fall: "Adresse statt Datensatz-ID",
    nachrichten: oberflaeche({
      ...liedKarte,
      liedId: "https://boese.example/lied",
    }),
  },
  {
    fall: "fremder Katalog",
    nachrichten: [
      {
        version: "v0.9",
        createSurface: {
          surfaceId: "s1",
          catalogId: "https://a2ui.org/specification/v0_9/basic_catalog.json",
        },
      },
      {
        version: "v0.9",
        updateComponents: {
          surfaceId: "s1",
          components: [{ id: "root", ...liedKarte }],
        },
      },
    ],
  },
  { fall: "keine Nachrichten, sondern ein Text", nachrichten: "Karte bitte" },
  { fall: "Nachrichten ohne Aufbau", nachrichten: [null, 7, "x", {}, []] },
];

for (const { fall, nachrichten } of kaputteOberflaechen) {
  test(`Ein falscher Client sendet ${fall}: nichts davon wird gezeigt`, async ({
    page,
  }) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await mockArchiv(page);
    await mockLauf(
      page,
      antwortStrom(threadId, ["Antwort ohne Karte."], {
        oberflaechen: nachrichten,
      }),
    );

    await stelleFrage(page);

    await expect(page.getByText("Antwort ohne Karte.")).toBeVisible();
    await expect(page.locator(".a2ui-oberflaechen")).toHaveCount(0);
    await expect(page.getByRole("article", { name: /^Lied:/ })).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Jetzt klicken" }),
    ).toHaveCount(0);
    await expect(page.getByText("Basis-Text")).toHaveCount(0);
    await expect(page.getByRole("link", { name: /boese/ })).toHaveCount(0);
    expect(errors).toEqual([]);
  });
}

test("Gemischt gesendet erscheint nur die gültige Karte", async ({ page }) => {
  await mockArchiv(page);
  await mockLauf(
    page,
    antwortStrom(threadId, ["Eine gute, eine schlechte."], {
      oberflaechen: [
        ...oberflaeche({ component: "Button", text: "Jetzt klicken" }, "s1"),
        ...oberflaeche(liedKarte, "s2"),
      ],
    }),
  );

  await stelleFrage(page);

  await expect(
    page.getByRole("article", { name: `Lied: ${titel}` }),
  ).toHaveCount(1);
  await expect(page.getByRole("button", { name: "Jetzt klicken" })).toHaveCount(
    0,
  );
});

test("Eine Antwort nur aus Karte ohne Text zeigt die Karte", async ({
  page,
}) => {
  await mockArchiv(page);
  const strom = antwortStrom(threadId, [], {
    oberflaechen: oberflaeche(liedKarte),
  }).replace(
    /data: \{"type":"TEXT_MESSAGE_START".*\n\n|data: \{"type":"TEXT_MESSAGE_END".*\n\n/g,
    "",
  );
  await mockLauf(page, strom);

  await stelleFrage(page);

  await expect(
    page.getByRole("article", { name: `Lied: ${titel}` }),
  ).toBeVisible();
  await expect(page.getByText("Das Archiv wird durchsucht")).toHaveCount(0);
});

test("Die übrigen Komponenten des Katalogs zeigen ihre Angaben und Verweise", async ({
  page,
}) => {
  await mockArchiv(page);
  await mockLauf(
    page,
    antwortStrom(threadId, ["Mehrere Karten."], {
      oberflaechen: [
        ...oberflaeche(
          {
            component: "AuftrittKarte",
            auftrittId,
            titel: "Frühlingskonzert 1932",
            art: "concert",
            datum: "Mai 1932",
            ort: "Festsaal",
          },
          "s1",
        ),
        ...oberflaeche(
          {
            component: "ProgrammListe",
            auftrittId,
            titel: "Frühlingskonzert 1932",
            punkte: [
              { position: 1, liedId, titel },
              { position: 2, liedId, titel: "Zweiter Satz", notiz: "Zugabe" },
            ],
          },
          "s2",
        ),
        ...oberflaeche(
          {
            component: "Quellen",
            quellen: [{ liedId, titel, komponist: "Carl Friedrich Zöllner" }],
          },
          "s3",
        ),
      ],
    }),
  );

  await stelleFrage(page);

  const auftritt = page.getByRole("article", {
    name: "Auftritt: Frühlingskonzert 1932",
  });
  await expect(auftritt.getByText("Konzert", { exact: true })).toBeVisible();
  await expect(auftritt.getByText("Mai 1932")).toBeVisible();
  await expect(auftritt.getByText("Festsaal")).toBeVisible();
  await expect(
    auftritt.getByRole("link", {
      name: "Auftritt im Archiv öffnen: Frühlingskonzert 1932",
    }),
  ).toHaveAttribute("href", `/auftritt/?id=${auftrittId}`);
  const programm = page.getByRole("region", {
    name: "Programm: Frühlingskonzert 1932",
  });
  await expect(programm.getByRole("listitem")).toHaveCount(2);
  await expect(
    programm.getByRole("link", { name: "Zweiter Satz" }),
  ).toHaveAttribute("href", `/lied/?id=${liedId}`);
  const quellen = page.getByRole("region", { name: "Lieder im Archiv" });
  await expect(
    quellen.getByRole("link", { name: `Lied im Archiv öffnen: ${titel}` }),
  ).toHaveAttribute("href", `/lied/?id=${liedId}`);
  // Die Karten entstehen erst nach dem Nachladen der Bibliothek; der Verlauf
  // wandert mit, sodass die letzte Karte nicht unter dem Rand verschwindet.
  await expect(quellen).toBeInViewport({ ratio: 0.5 });
});
