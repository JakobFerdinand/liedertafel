import { expect, type Page, test } from "@playwright/test";

const memberMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000002",
  email: "mitglied@liedertafel.test",
  displayName: "Testmitglied",
  roles: ["Member"],
  verifiedAt: new Date().toISOString(),
};

const threadId = "00000000-0000-0000-0000-00000000c0de";
const wandernId = "00000000-0000-0000-0000-0000000002a1";

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

// AG-UI-Ereignisstrom als Text: ein JSON-Objekt je data:-Zeile (ARC-022).
function sse(ereignisse: Array<Record<string, unknown>>): string {
  return ereignisse
    .map((ereignis) => `data: ${JSON.stringify(ereignis)}\n\n`)
    .join("");
}

async function mockSitzung(page: Page) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(memberMe)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}

async function mockArchiv(page: Page) {
  await mockSitzung(page);
  const lied = {
    id: wandernId,
    title: "Das Wandern ist des Müllers Lust",
    composer: "Carl Friedrich Zöllner",
    lyricist: "Wilhelm Müller",
    published: true,
    publishedAt: "2026-09-01T10:00:00.000Z",
  };
  await page.route("**/api/songs", (route) =>
    route.fulfill(json({ songs: [lied] })),
  );
  await page.route(`**/api/songs/${wandernId}`, (route) =>
    route.fulfill(
      json({
        song: {
          ...lied,
          createdAt: "2026-08-20T08:00:00.000Z",
          updatedAt: "2026-09-01T10:00:00.000Z",
          arrangements: [],
        },
      }),
    ),
  );
  // Ein versehentlicher Remount darf den lokalen Verlauf nicht durch einen
  // erneuten Abruf ersetzen; der Mock macht diesen Verlust sichtbar.
  await page.route(/\/api\/chat\/thread\/[0-9a-f-]+$/, (route) =>
    route.fulfill(json({ threadId, messages: [] })),
  );
}

type ChatAnfrage = {
  threadId: string;
  runId: string;
  messages: Array<{ id: string; role: string; content: string }>;
};

function erfolgreicheAntwort(label: string) {
  return sse([
    { type: "RUN_STARTED", threadId, runId: "run-1" },
    {
      type: "TOOL_CALL_START",
      threadId,
      runId: "run-1",
      toolCallId: "tool-1",
      toolCallName: "archiv_recherche",
    },
    {
      type: "TOOL_CALL_ARGS",
      toolCallId: "tool-1",
      delta: '{"titel":"Wandern"}',
    },
    { type: "TOOL_CALL_END", toolCallId: "tool-1" },
    { type: "TEXT_MESSAGE_START", threadId, runId: "run-1", messageId: "m1" },
    {
      type: "TEXT_MESSAGE_CONTENT",
      messageId: "m1",
      delta: "Das Lied stammt ",
    },
    {
      type: "TEXT_MESSAGE_CONTENT",
      messageId: "m1",
      delta: `von Carl Friedrich Zöllner. (${label})`,
    },
    { type: "TEXT_MESSAGE_END", messageId: "m1" },
    {
      type: "CUSTOM",
      name: "archive.citations",
      value: [{ id: wandernId, label: "Das Wandern ist des Müllers Lust" }],
    },
    {
      type: "RUN_FINISHED",
      threadId,
      runId: "run-1",
      usage: { inputTokens: 12, outputTokens: 34 },
    },
  ]);
}

test("Frage wird beantwortet und nennt das zitierte Lied als Quelle", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page);
  const anfragen: ChatAnfrage[] = [];
  await page.route(/\/api\/chat$/, (route) => {
    anfragen.push(route.request().postDataJSON() as ChatAnfrage);
    return route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: erfolgreicheAntwort("Erster Lauf"),
    });
  });

  await page.goto("/fragen/");
  await expect(
    page.getByRole("heading", { name: "Was möchtest du wissen?" }),
  ).toBeVisible();
  await page
    .getByLabel("Frage stellen")
    .fill("Wer komponierte das Wandernlied?");
  await page.getByRole("button", { name: "Absenden" }).click();

  await expect(
    page.getByText(
      "Das Lied stammt von Carl Friedrich Zöllner. (Erster Lauf)",
      { exact: true },
    ),
  ).toBeVisible();
  await expect(page.getByText("Antwort erscheint hier …")).toHaveCount(0);
  const quelle = page.getByRole("link", {
    name: "Quelle: Das Wandern ist des Müllers Lust",
  });
  await expect(quelle).toBeVisible();
  await expect(quelle).toHaveAttribute(
    "href",
    new RegExp(`lied/\\?id=${wandernId}`),
  );

  expect(anfragen).toHaveLength(1);
  expect(anfragen[0].messages).toHaveLength(1);
  expect(anfragen[0].messages[0].role).toBe("user");
  expect(anfragen[0].messages[0].content).toBe(
    "Wer komponierte das Wandernlied?",
  );
  expect(anfragen[0].threadId).toMatch(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
  );
  await expect
    .poll(() =>
      page.evaluate(() => window.localStorage.getItem("arc-chat-thread")),
    )
    .toBe(threadId);

  expect(errors).toEqual([]);
});

test("Gespeicherter Verlauf wird wiederhergestellt und nur die neue Frage gesendet", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page);
  await page.addInitScript(
    ([id]) => {
      window.localStorage.setItem("arc-chat-thread", id);
    },
    [threadId],
  );
  await page.route(/\/api\/chat\/thread\/[0-9a-f-]+$/, (route) =>
    route.fulfill(
      json({
        threadId,
        messages: [
          { role: "user", content: "Wer komponierte das Wandernlied?" },
          {
            role: "assistant",
            content:
              "Das Lied stammt von Carl Friedrich Zöllner. [Quelle: Das Wandern ist des Müllers Lust]",
          },
        ],
      }),
    ),
  );
  const anfragen: ChatAnfrage[] = [];
  await page.route(/\/api\/chat$/, (route) => {
    anfragen.push(route.request().postDataJSON() as ChatAnfrage);
    return route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: erfolgreicheAntwort("Fortsetzung"),
    });
  });

  await page.goto("/fragen/");
  await expect(
    page.getByText("Wer komponierte das Wandernlied?", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Das Lied stammt von Carl Friedrich Zöllner. [Quelle: Das Wandern ist des Müllers Lust]",
      { exact: true },
    ),
  ).toBeVisible();
  // Die Historie liefert keine Quellenmetadaten: keine erfundenen Links.
  await expect(page.getByRole("log").getByRole("link")).toHaveCount(0);

  await page.getByLabel("Frage stellen").fill("Welche Fassungen gibt es?");
  await page.getByRole("button", { name: "Absenden" }).click();
  await expect(
    page.getByText(
      "Das Lied stammt von Carl Friedrich Zöllner. (Fortsetzung)",
      { exact: true },
    ),
  ).toBeVisible();
  expect(anfragen).toHaveLength(1);
  // Nur die neue Frage geht an den Server; die Historie bleibt serverseitig.
  expect(anfragen[0].messages).toEqual([
    expect.objectContaining({
      role: "user",
      content: "Welche Fassungen gibt es?",
    }),
  ]);

  expect(errors).toEqual([]);
});

test("Vorgeschlagene Fragen, vollständiger Composer und neuer Chat funktionieren auch mobil", async ({
  page,
}) => {
  await mockSitzung(page);
  const anfragen: ChatAnfrage[] = [];
  await page.route(/\/api\/chat$/, (route) => {
    anfragen.push(route.request().postDataJSON() as ChatAnfrage);
    return route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: erfolgreicheAntwort("Vorschlag"),
    });
  });
  await page.goto("/fragen/");
  const eingabe = page.getByLabel("Frage stellen");
  await expect(eingabe).toBeVisible();
  const breite = await eingabe.evaluate((feld) => ({
    feld: feld.getBoundingClientRect().width,
    formular: feld.closest("form")?.getBoundingClientRect().width ?? 0,
    seite: document.documentElement.scrollWidth,
    fenster: window.innerWidth,
  }));
  expect(breite.feld / breite.formular).toBeGreaterThan(0.85);
  expect(breite.seite).toBeLessThanOrEqual(breite.fenster);
  await expect(page.getByRole("button", { name: "Absenden" })).toBeDisabled();
  await page
    .getByRole("button", { name: "Welche Lieder gibt es?", exact: true })
    .click();
  await expect(
    page.getByText("Das Lied stammt von Carl Friedrich Zöllner. (Vorschlag)"),
  ).toBeVisible();
  expect(anfragen[0].messages[0].content).toBe("Welche Lieder gibt es?");
  await expect(page.getByText("Du", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("log").getByText("Archiv", { exact: true }),
  ).toBeVisible();

  await page.getByRole("button", { name: "Neuer Chat" }).click();
  await expect(
    page.getByRole("button", { name: "Welche Lieder gibt es?", exact: true }),
  ).toBeVisible();
  await expect(eingabe).toBeFocused();
  expect(
    await page.evaluate(() => localStorage.getItem("arc-chat-thread")),
  ).toBeNull();
  await eingabe.fill("Welche Fassungen gibt es?");
  await eingabe.press("Control+Enter");
  await expect.poll(() => anfragen.length).toBe(2);
  expect(anfragen[1].threadId).not.toBe(anfragen[0].threadId);
});

test("Nur belegte Quellenmarker werden verlinkt; Modelltext bleibt sicherer Text", async ({
  page,
}) => {
  await mockSitzung(page);
  await page.route(/\/api\/chat$/, (route) =>
    route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: sse([
        { type: "RUN_STARTED", threadId },
        {
          type: "TEXT_MESSAGE_CONTENT",
          delta:
            "Im Bestand:\n\n- **Das Wandern** [Quelle: Das Wandern ist des Müllers Lust]\n- Unbelegter Hinweis [Quelle: erfunden]\n\n<img src=x onerror=alert(1)> [Externe Seite](javascript:alert(1))",
        },
        {
          type: "CUSTOM",
          name: "archive.citations",
          value: [
            {
              id: wandernId,
              label: "Das Wandern ist des Müllers Lust",
            },
            {
              id: "javascript:alert(1)",
              label: "erfunden",
            },
          ],
        },
        { type: "RUN_FINISHED", threadId },
      ]),
    }),
  );
  await page.goto("/fragen/");
  await page
    .getByRole("button", { name: "Welche Lieder gibt es?", exact: true })
    .click();
  const verlauf = page.getByRole("log");
  await expect(
    verlauf.getByRole("link", {
      name: "Quelle öffnen: Das Wandern ist des Müllers Lust",
    }),
  ).toHaveAttribute("href", `/lied/?id=${wandernId}`);
  await expect(verlauf).not.toContainText(
    "[Quelle: Das Wandern ist des Müllers Lust]",
  );
  await expect(verlauf).toContainText("[Quelle: erfunden]");
  await expect(verlauf.locator("strong")).toHaveText("Das Wandern");
  await expect(verlauf.locator("img")).toHaveCount(0);
  await expect(verlauf.locator('a[href^="javascript:"]')).toHaveCount(0);
  await expect(verlauf).toContainText("<img src=x onerror=alert(1)>");
});

test("Belegte Quellen mit Anführungszeichen, Umlauten und Klammern werden vollständig verlinkt", async ({
  page,
}) => {
  await mockSitzung(page);
  const quellen = [
    { id: wandernId, label: "Die Waldfahrt" },
    { id: "00000000-0000-0000-0000-0000000002a2", label: "Abendlied [SATB]" },
    {
      id: "00000000-0000-0000-0000-0000000002a3",
      label: "Müllers Wander-Lied",
    },
  ];
  await page.route(/\/api\/chat$/, (route) =>
    route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: sse([
        { type: "RUN_STARTED", threadId },
        {
          type: "TEXT_MESSAGE_CONTENT",
          delta:
            "Die Auswahl: [Quelle: „Die Waldfahrt“] [Quelle: Abendlied [SATB]] [Quelle: Mueller's Wander Lied].\n\nUnbelegt: [Quelle: Abendlied [Unbekannt]] [Quelle: „Fremdes Lied“].",
        },
        { type: "CUSTOM", name: "archive.citations", value: quellen },
        { type: "RUN_FINISHED", threadId },
      ]),
    }),
  );
  await page.goto("/fragen/");
  await page
    .getByRole("button", { name: "Welche Lieder gibt es?", exact: true })
    .click();
  const antwort = page.locator(".chat-antwort-text");
  for (const quelle of quellen) {
    await expect(
      antwort.getByRole("link", {
        name: `Quelle öffnen: ${quelle.label}`,
        exact: true,
      }),
    ).toHaveAttribute("href", `/lied/?id=${quelle.id}`);
  }
  await expect(antwort).toHaveText(
    "Die Auswahl: [1] [2] [3].Unbelegt: [Quelle: Abendlied [Unbekannt]] [Quelle: „Fremdes Lied“].",
  );
  await expect(antwort.getByRole("link")).toHaveCount(3);
});

test("Der Composer beginnt auf einem kleinen Mobilbildschirm ohne Scrollen sichtbar", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockSitzung(page);
  await page.goto("/fragen/");
  await expect(
    page.getByRole("button", { name: "Welche Lieder sind von Silcher?" }),
  ).toBeVisible();
  const eingabe = page.getByLabel("Frage stellen");
  await expect(eingabe).toBeInViewport({ ratio: 0.5 });
  expect(await page.evaluate(() => window.scrollY)).toBe(0);
});

test("Abbrechen hält den Entwurf und verhindert verspätete Antworten im neuen Chat", async ({
  page,
}) => {
  await mockSitzung(page);
  let freigeben = () => {};
  const warte = new Promise<void>((resolve) => {
    freigeben = resolve;
  });
  let anfragen = 0;
  await page.route(/\/api\/chat$/, async (route) => {
    anfragen++;
    if (anfragen === 1) await warte;
    await route
      .fulfill({
        status: 200,
        contentType: "text/event-stream",
        body: erfolgreicheAntwort(anfragen === 1 ? "Abgebrochen" : "Neustart"),
      })
      .catch(() => {});
  });
  await page.goto("/fragen/");
  await page
    .getByRole("button", { name: "Welche Lieder gibt es?", exact: true })
    .click();
  await expect(page.getByText("Das Archiv wird durchsucht …")).toBeVisible();
  await expect(page.getByRole("button", { name: "Neuer Chat" })).toBeDisabled();
  await page.getByLabel("Frage stellen").fill("Mein nächster Gedanke");
  await page.getByRole("button", { name: "Abbrechen" }).click();
  await expect(page.getByLabel("Frage stellen")).toHaveValue(
    "Mein nächster Gedanke",
  );
  await expect(
    page.getByText("Antwort abgebrochen.", { exact: false }),
  ).toBeVisible();
  await expect(page.getByText("Das Archiv wird durchsucht …")).toHaveCount(0);
  await page.getByRole("button", { name: "Neuer Chat" }).click();
  freigeben();
  await page
    .getByRole("button", { name: "Welche Lieder gibt es?", exact: true })
    .click();
  await expect(
    page.getByText("Das Lied stammt von Carl Friedrich Zöllner. (Neustart)"),
  ).toBeVisible();
  await expect(page.getByRole("log")).not.toContainText("Abgebrochen");
  await expect(page.getByRole("log").locator("article")).toHaveCount(2);
});

test("Während der Verlauf lädt bleiben neue Fragen gesperrt; Zeichenlimit wird erklärt", async ({
  page,
}) => {
  await mockSitzung(page);
  await page.addInitScript(
    (id) => localStorage.setItem("arc-chat-thread", id),
    threadId,
  );
  let freigeben = () => {};
  const warte = new Promise<void>((resolve) => {
    freigeben = resolve;
  });
  await page.route(/\/api\/chat\/thread\//, async (route) => {
    await warte;
    return route.fulfill(json({ threadId, messages: [] }));
  });
  await page.goto("/fragen/");
  await expect(page.getByText("Dein Gespräch wird geladen …")).toBeVisible();
  await expect(page.getByLabel("Frage stellen")).toHaveCount(0);
  freigeben();
  const eingabe = page.getByLabel("Frage stellen");
  await eingabe.fill("a".repeat(2000));
  await expect(eingabe).toHaveAttribute("maxlength", "2000");
  await expect(eingabe).toHaveAccessibleDescription(/Noch 0 Zeichen übrig/);
  await expect(page.getByText("Noch 0 Zeichen übrig.")).toBeVisible();
});

test("Wiederholen einer Teilantwort ersetzt den Versuch ohne doppelte Frage", async ({
  page,
}) => {
  await mockSitzung(page);
  let versuche = 0;
  await page.route(/\/api\/chat$/, (route) => {
    versuche++;
    return route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body:
        versuche === 1
          ? sse([
              { type: "RUN_STARTED", threadId },
              { type: "TEXT_MESSAGE_CONTENT", delta: "Unvollständige Antwort" },
              {
                type: "RUN_ERROR",
                message: "Die Antwort konnte nicht fertig gestellt werden.",
              },
            ])
          : erfolgreicheAntwort("Wiederholt"),
    });
  });
  await page.goto("/fragen/");
  await page
    .getByRole("button", { name: "Welche Lieder gibt es?", exact: true })
    .click();
  await expect(page.getByText("Unvollständige Antwort")).toBeVisible();
  await page.getByLabel("Frage stellen").fill("Mein nächster Gedanke");
  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(
    page.getByText("Das Lied stammt von Carl Friedrich Zöllner. (Wiederholt)"),
  ).toBeVisible();
  await expect(page.getByText("Unvollständige Antwort")).toHaveCount(0);
  await expect(page.getByLabel("Frage stellen")).toHaveValue(
    "Mein nächster Gedanke",
  );
  await expect(page.getByRole("log").locator("article")).toHaveCount(2);
});

test("Nicht verfügbarer Chat erklärt sich auf Deutsch und lässt sich wiederholen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page);
  let verfuegbar = false;
  await page.route(/\/api\/chat$/, (route) => {
    if (!verfuegbar) {
      return route.fulfill(
        problem("Der Archiv-Chat ist derzeit nicht verfügbar.", 503),
      );
    }
    return route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: erfolgreicheAntwort("Zweiter Versuch"),
    });
  });

  await page.goto("/fragen/");
  await page
    .getByLabel("Frage stellen")
    .fill("Wer komponierte das Wandernlied?");
  await page.getByRole("button", { name: "Absenden" }).click();
  await expect(
    page.getByText("Der Archiv-Chat ist derzeit nicht verfügbar."),
  ).toBeVisible();
  await expect(page.getByText("Antwort erscheint hier …")).toHaveCount(0);

  verfuegbar = true;
  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(
    page.getByText(
      "Das Lied stammt von Carl Friedrich Zöllner. (Zweiter Versuch)",
      { exact: true },
    ),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Ein RUN_ERROR-Ereignis zeigt die deutsche Fehlmeldung mit erneutem Versuch", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page);
  await page.route(/\/api\/chat$/, (route) =>
    route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: sse([
        { type: "RUN_STARTED", threadId, runId: "run-1" },
        {
          type: "RUN_ERROR",
          message: "Die Antwort konnte nicht fertig gestellt werden.",
        },
      ]),
    }),
  );

  await page.goto("/fragen/");
  await page
    .getByLabel("Frage stellen")
    .fill("Wer komponierte das Wandernlied?");
  await page.getByRole("button", { name: "Absenden" }).click();
  await expect(
    page.getByText("Die Antwort konnte nicht fertig gestellt werden."),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Ein fremder Chatverlauf wird verworfen und der Chat startet frisch", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page);
  await page.addInitScript(
    ([id]) => {
      window.localStorage.setItem("arc-chat-thread", id);
    },
    [threadId],
  );
  await page.route(/\/api\/chat\/thread\/[0-9a-f-]+$/, (route) =>
    route.fulfill(problem("Keine Berechtigung für diesen Chatverlauf.", 404)),
  );
  await page.route(/\/api\/chat$/, (route) =>
    route.fulfill(problem("Keine Berechtigung für diesen Chatverlauf.", 403)),
  );

  await page.goto("/fragen/");
  await page
    .getByLabel("Frage stellen")
    .fill("Wer komponierte das Wandernlied?");
  await page.getByRole("button", { name: "Absenden" }).click();
  await expect(
    page.getByText("Der bisherige Verlauf wurde verworfen", { exact: false }),
  ).toBeVisible();
  await expect(page.getByLabel("Frage stellen")).toHaveValue(
    "Wer komponierte das Wandernlied?",
  );
  await expect
    .poll(() =>
      page.evaluate(() => window.localStorage.getItem("arc-chat-thread")),
    )
    .toBeNull();

  expect(errors).toEqual([]);
});

test("Der Katalog bietet Chat direkt an; Minimieren erhält Entwurf und Fokus", async ({
  page,
  isMobile,
}) => {
  await mockArchiv(page);
  await page.goto("/lieder/");
  const katalog = page.getByRole("heading", {
    name: "Liederkatalog",
    level: 1,
  });
  const eingabe = page.getByLabel("Frage stellen");
  const oeffnen = page.getByRole("button", {
    name: "Archiv fragen",
    exact: true,
  });
  await expect(katalog).toBeVisible();
  if (isMobile) {
    await expect(eingabe).toBeHidden();
    await expect(oeffnen).toBeInViewport();
    await oeffnen.click();
    await expect(katalog).toBeHidden();
  }
  await expect(eingabe).toBeVisible();
  await expect(eingabe).toHaveCount(1);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page).toHaveURL(/\/lieder\/$/);
  if (!isMobile) await expect(katalog).toBeVisible();

  await eingabe.fill(
    "Welche Lieder eignen sich für unseren nächsten Auftritt?",
  );
  await page.getByRole("button", { name: "Chat minimieren" }).click();
  await expect(eingabe).toBeHidden();
  await expect(katalog).toBeVisible();
  await expect(oeffnen).toBeFocused();
  await oeffnen.click();
  await expect(eingabe).toHaveValue(
    "Welche Lieder eignen sich für unseren nächsten Auftritt?",
  );
  await expect(eingabe).toBeVisible();
  await expect(page).toHaveURL(/\/lieder\/$/);
});

test("Großansicht und Katalog teilen Gespräch, Thread und ungesendeten Entwurf", async ({
  page,
  isMobile,
}) => {
  await mockArchiv(page);
  const anfragen: ChatAnfrage[] = [];
  const dokumente: string[] = [];
  page.on("request", (request) => {
    if (request.isNavigationRequest() && request.resourceType() === "document")
      dokumente.push(request.url());
  });
  await page.route(/\/api\/chat$/, (route) => {
    anfragen.push(route.request().postDataJSON() as ChatAnfrage);
    return route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: erfolgreicheAntwort(`Navigation ${anfragen.length}`),
    });
  });
  await page.goto("/fragen/");
  const eingabe = page.getByLabel("Frage stellen");
  await eingabe.fill("Wer komponierte das Wandernlied?");
  await page.getByRole("button", { name: "Absenden" }).click();
  const ersteAntwort = page.getByText(
    "Das Lied stammt von Carl Friedrich Zöllner. (Navigation 1)",
    { exact: true },
  );
  await expect(ersteAntwort).toBeVisible();
  await eingabe.fill("Welche Fassungen gibt es?");

  await page
    .getByRole("navigation", { name: "Hauptnavigation" })
    .getByRole("link", { name: "Liederkatalog", exact: true })
    .click();
  await expect(page).toHaveURL(/\/lieder\/$/);
  if (isMobile && !(await eingabe.isVisible()))
    await page
      .getByRole("button", { name: "Archiv fragen", exact: true })
      .click();
  await expect(ersteAntwort).toBeVisible();
  await expect(eingabe).toHaveValue("Welche Fassungen gibt es?");
  await expect(eingabe).toHaveCount(1);

  await page.getByRole("link", { name: "Großansicht", exact: true }).click();
  await expect(page).toHaveURL(/\/fragen\/$/);
  await expect(ersteAntwort).toBeVisible();
  await expect(eingabe).toHaveValue("Welche Fassungen gibt es?");
  await expect(eingabe).toHaveCount(1);
  await page.getByRole("button", { name: "Absenden" }).click();
  await expect(
    page.getByText(
      "Das Lied stammt von Carl Friedrich Zöllner. (Navigation 2)",
      {
        exact: true,
      },
    ),
  ).toBeVisible();
  await expect(ersteAntwort).toBeVisible();
  await expect(
    page.getByRole("log").getByText("Wer komponierte das Wandernlied?", {
      exact: true,
    }),
  ).toHaveCount(1);
  expect(anfragen).toHaveLength(2);
  expect(anfragen[1].threadId).toBe(threadId);
  expect(anfragen[1].messages).toEqual([
    expect.objectContaining({
      role: "user",
      content: "Welche Fassungen gibt es?",
    }),
  ]);
  // Links müssen clientseitig navigieren, damit auch flüchtiger Zustand bleibt.
  expect(dokumente).toHaveLength(1);
});

test("Eine verzögerte Chatantwort überlebt Navigation und Minimieren", async ({
  page,
  isMobile,
}) => {
  await mockArchiv(page);
  let freigeben = () => {};
  const warte = new Promise<void>((resolve) => {
    freigeben = resolve;
  });
  const anfragen: ChatAnfrage[] = [];
  await page.route(/\/api\/chat$/, async (route) => {
    anfragen.push(route.request().postDataJSON() as ChatAnfrage);
    await warte;
    await route.fulfill({
      status: 200,
      contentType: "text/event-stream",
      body: erfolgreicheAntwort("Nach Navigation"),
    });
  });

  try {
    await page.goto("/fragen/");
    await page
      .getByLabel("Frage stellen")
      .fill("Wer komponierte das Wandernlied?");
    await page.getByRole("button", { name: "Absenden" }).click();
    await expect.poll(() => anfragen.length).toBe(1);
    await expect(page.getByRole("button", { name: "Abbrechen" })).toBeVisible();
    await page.getByLabel("Frage stellen").fill("Mein nächster Gedanke");

    await page
      .getByRole("navigation", { name: "Hauptnavigation" })
      .getByRole("link", { name: "Liederkatalog", exact: true })
      .click();
    await expect(page).toHaveURL(/\/lieder\/$/);
    if (isMobile && !(await page.getByLabel("Frage stellen").isVisible()))
      await page
        .getByRole("button", { name: "Archiv fragen", exact: true })
        .click();
    await expect(page.getByRole("button", { name: "Abbrechen" })).toBeVisible();
    await page.getByRole("button", { name: "Chat minimieren" }).click();
    await page
      .getByRole("button", { name: "Archiv fragen", exact: true })
      .click();
    await expect(page.getByLabel("Frage stellen")).toHaveValue(
      "Mein nächster Gedanke",
    );
    await expect(page.getByRole("button", { name: "Abbrechen" })).toBeVisible();
    await page.getByRole("link", { name: "Großansicht", exact: true }).click();
    await expect(page).toHaveURL(/\/fragen\/$/);
    await expect(page.getByRole("button", { name: "Abbrechen" })).toBeVisible();
    freigeben();

    await expect(
      page.getByText(
        "Das Lied stammt von Carl Friedrich Zöllner. (Nach Navigation)",
        {
          exact: true,
        },
      ),
    ).toBeVisible();
    await expect(page.getByLabel("Frage stellen")).toHaveValue(
      "Mein nächster Gedanke",
    );
    await expect(page.getByRole("button", { name: "Abbrechen" })).toHaveCount(
      0,
    );
    await expect(page.getByRole("button", { name: "Absenden" })).toBeEnabled();
    await expect(
      page.getByRole("log").getByText("Wer komponierte das Wandernlied?", {
        exact: true,
      }),
    ).toHaveCount(1);
    expect(anfragen).toHaveLength(1);
    expect(
      await page.evaluate(() => localStorage.getItem("arc-chat-thread")),
    ).toBe(threadId);
  } finally {
    freigeben();
  }
});

for (const quellenLink of ["Quelle", "Quelle öffnen"]) {
  test(`${quellenLink} öffnet das Lied und erhält den Chat`, async ({
    page,
    isMobile,
  }) => {
    await mockArchiv(page);
    const anfragen: ChatAnfrage[] = [];
    await page.route(/\/api\/chat$/, (route) => {
      anfragen.push(route.request().postDataJSON() as ChatAnfrage);
      return route.fulfill({
        status: 200,
        contentType: "text/event-stream",
        body: sse([
          { type: "RUN_STARTED", threadId },
          {
            type: "TEXT_MESSAGE_CONTENT",
            delta:
              "Das Lied stammt von Carl Friedrich Zöllner. [Quelle: Das Wandern ist des Müllers Lust]",
          },
          {
            type: "CUSTOM",
            name: "archive.citations",
            value: [
              { id: wandernId, label: "Das Wandern ist des Müllers Lust" },
            ],
          },
          { type: "RUN_FINISHED", threadId },
        ]),
      });
    });
    await page.goto("/lieder/");
    if (isMobile)
      await page
        .getByRole("button", { name: "Archiv fragen", exact: true })
        .click();
    await page
      .getByLabel("Frage stellen")
      .fill("Wer komponierte das Wandernlied?");
    await page.getByRole("button", { name: "Absenden" }).click();
    const quelle = page.getByRole("link", {
      name: `${quellenLink}: Das Wandern ist des Müllers Lust`,
      exact: true,
    });
    await expect(quelle).toBeVisible();
    await page.getByLabel("Frage stellen").fill("Welche Fassungen gibt es?");
    await quelle.click();
    await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${wandernId}$`));
    await expect(
      page.getByRole("heading", {
        name: "Das Wandern ist des Müllers Lust",
        exact: true,
      }),
    ).toBeVisible();
    if (isMobile) {
      await expect(page.getByLabel("Frage stellen")).toBeHidden();
      await page
        .getByRole("button", { name: "Archiv fragen", exact: true })
        .click();
    }
    await expect(page.getByLabel("Frage stellen")).toBeVisible();
    await expect(page.getByLabel("Frage stellen")).toHaveValue(
      "Welche Fassungen gibt es?",
    );
    await expect(page.getByRole("log")).toContainText(
      "Das Lied stammt von Carl Friedrich Zöllner.",
    );
    await expect(quelle).toHaveCount(1);
    await page.getByRole("button", { name: "Absenden" }).click();
    await expect.poll(() => anfragen.length).toBe(2);
    expect(anfragen[1].threadId).toBe(threadId);
    expect(anfragen[1].messages).toEqual([
      expect.objectContaining({
        role: "user",
        content: "Welche Fassungen gibt es?",
      }),
    ]);
  });
}
