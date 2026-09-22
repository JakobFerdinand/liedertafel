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
    page.getByText("Stelle deine erste Frage an das Archiv"),
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
            content: "Das Lied stammt von Carl Friedrich Zöllner.",
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
    page.getByText("Das Lied stammt von Carl Friedrich Zöllner.", {
      exact: true,
    }),
  ).toBeVisible();

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
