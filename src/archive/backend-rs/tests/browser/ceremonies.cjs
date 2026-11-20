// Real Chromium + unchanged static frontend; no successful-verification fake.
// Synthetic-only fixture generation: run against the frozen .NET origin.
const { chromium, expect } = require("@playwright/test");
const { Client } = require("pg");
const fs = require("node:fs/promises");
const assert = require("node:assert/strict");
const path = require("node:path");

const url = process.env.ARCHIVE_BASE_URL;
const mail = process.env.ARCHIVE_MAIL_URL;
const dbUrl = process.env.ARCHIVE_AUTH_DATABASE;
const fixturePath = path.resolve(__dirname, "../fixtures/dotnet-es256.json");
const mode = process.argv[2] || "enroll";

async function main() {
  assert(url && mail && dbUrl, "explicit local-only browser/mail/throwaway database configuration required");
  for (const endpoint of [url,mail,dbUrl]) assert(["localhost","127.0.0.1","[::1]"].includes(new URL(endpoint).hostname),"refuses nonlocal endpoints");
  assert(["enroll","legacy","generate"].includes(mode),"unknown test mode");
  const db = new Client({ connectionString: dbUrl });
  await db.connect();
  const browser = await chromium.launch({ executablePath: process.env.ARCHIVE_CHROMIUM });
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(error.message));
    const cdp = await context.newCDPSession(page);
    await cdp.send("WebAuthn.enable", { enableUI: false });
    const { authenticatorId } = await cdp.send("WebAuthn.addVirtualAuthenticator", {
      options: { protocol:"ctap2", transport:"internal", hasResidentKey:true, hasUserVerification:true, isUserVerified:true, automaticPresenceSimulation:true },
    });
    if (mode === "legacy") {
      const fixture = JSON.parse(await fs.readFile(fixturePath));
      assert.equal(fixture.syntheticOnly, true);
      const user = fixture.user;
      await db.query(`INSERT INTO "AspNetUsers"("Id","DisplayName","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","SecurityStamp","ConcurrencyStamp") VALUES($1,$2,$3,$4,$3,$4,true,false,false,true,0,'synthetic-fixture','synthetic-fixture') ON CONFLICT("Id") DO NOTHING`, [user.Id,user.DisplayName,user.Email,user.NormalizedEmail]);
      await db.query(`INSERT INTO "AspNetRoles"("Id","Name","NormalizedName") VALUES('00000000-0000-0000-0000-000000000221','Member','MEMBER') ON CONFLICT DO NOTHING`);
      await db.query(`INSERT INTO "AspNetUserRoles"("UserId","RoleId") SELECT $1,"Id" FROM "AspNetRoles" WHERE "Name"='Member' ON CONFLICT DO NOTHING`,[user.Id]);
      // Import unchanged Identity JSON, not a Rust-native credential.
      await db.query(`INSERT INTO "AspNetUserPasskeys"("CredentialId","UserId","Data") VALUES($1,$2,$3) ON CONFLICT("CredentialId") DO UPDATE SET "Data"=excluded."Data"`,[Buffer.from(fixture.credential.credentialId,"base64"),user.Id,fixture.data]);
      await cdp.send("WebAuthn.addCredential", { authenticatorId, credential:fixture.credential });
      await page.goto(`${url}/anmelden/`);
      await page.getByRole("button",{name:"Mit Passkey anmelden"}).click();
      await expect(page).toHaveURL(/\/archiv\//);
      await expect(page.getByText(`Angemeldet als ${user.DisplayName} (Member)`)).toBeVisible();
      const me = await page.evaluate(async () => (await fetch("/api/auth/me")).json());
      assert.equal(me.authMethod,"passkey");
      assert.equal(me.accountId,user.Id);
      const row = (await db.query(`SELECT "Data" FROM "AspNetUserPasskeys" WHERE "UserId"=$1`,[user.Id])).rows[0].Data;
      assert(row.SignCount > fixture.data.SignCount);
      for (const field of ["PublicKey","Name","CreatedAt","AttestationObject","ClientDataJson"]) assert.deepEqual(row[field],fixture.data[field]);
      // Session survives fresh browser context (native restart is tested separately).
      const storage = await context.storageState();
      const replica = await browser.newContext({ storageState:storage });
      const replicaPage = await replica.newPage();
      await replicaPage.goto(`${url}/archiv/`);
      await expect(replicaPage.getByText(`Angemeldet als ${user.DisplayName} (Member)`)).toBeVisible();
      await replica.close();
      console.log("PASS: synthetic .NET Identity ES256 old row + 36-byte handle + fresh Rust challenge, real browser signature, counter persistence, unchanged JSON");
    } else {
      const csrf = await context.request.get(`${url}/api/antiforgery`);
      const token = (await csrf.json()).token;
      assert((await context.request.post(`${url}/api/dev/auth/seed`, {headers:{"X-CSRF-TOKEN":token},data:{}})).ok());
      await context.request.delete(`${mail}/api/v1/messages`);
      await page.goto(`${url}/anmelden/`);
      await page.getByLabel("E-Mail-Adresse").fill("mitglied@liedertafel.test");
      await page.getByRole("button",{name:"Code anfordern"}).click();
      await expect(page.getByLabel("Code aus der E-Mail")).toBeVisible();
      let code;
      await expect.poll(async()=>{
        const {messages} = await (await context.request.get(`${mail}/api/v1/messages?limit=50`)).json();
        for (const message of messages || []) {
          const detail = await (await context.request.get(`${mail}/api/v1/message/${message.ID}`)).json();
          if (JSON.stringify(detail).toLowerCase().includes("mitglied@liedertafel.test")) code = /Ihr Anmeldecode lautet:\s*(\d{6})/.exec(detail.Text || "")?.[1];
        }
        return !!code;
      }).toBe(true);
      await page.getByLabel("Code aus der E-Mail").fill(code);
      await page.getByRole("button",{name:"Anmelden",exact:true}).click();
      await expect(page).toHaveURL(/\/archiv\//);
      await expect(page.getByText("Angemeldet als Testmitglied (Member)")).toBeVisible();
      await page.evaluate(async()=>{
        const {token} = await (await fetch("/api/antiforgery")).json();
        const {passkeys} = await (await fetch("/api/auth/passkeys")).json();
        for(const p of passkeys) {
          const res = await fetch("/api/auth/passkeys/remove",{method:"POST",headers:{"Content-Type":"application/json","X-CSRF-TOKEN":token},body:JSON.stringify({credentialId:p.credentialId})});
          if(!res.ok)throw new Error("cleanup failed");
        }
      });
      await page.reload();
      await expect(page.getByText("Noch kein Passkey registriert.")).toBeVisible();
      await page.getByLabel("Name für den neuen Passkey").fill("Synthetic test authenticator");
      await page.getByRole("button",{name:"Passkey hinzufügen"}).click();
      await expect(page.getByText("Passkey registriert.", { exact:true })).toBeVisible();
      if (mode === "generate") {
        const credentials = (await cdp.send("WebAuthn.getCredentials",{authenticatorId})).credentials;
        assert.equal(credentials.length,1);
        const {rows} = await db.query(`SELECT u."Id",u."Email",u."NormalizedEmail",u."DisplayName",p."Data" FROM "AspNetUsers" u JOIN "AspNetUserPasskeys" p ON p."UserId"=u."Id" WHERE u."NormalizedEmail"='MITGLIED@LIEDERTAFEL.TEST'`);
        assert.equal(rows.length,1);
        const {Data,...user}=rows[0];
        assert.equal(Buffer.from(credentials[0].userHandle,"base64").length,36);
        await fs.mkdir(path.dirname(fixturePath),{recursive:true});
        await fs.writeFile(fixturePath, JSON.stringify({syntheticOnly:true,provenance:"Frozen .NET 10 source at 0ff95e6, actual HTTP enrollment via unchanged frontend + Chromium CDP virtual ES256 authenticator; no production data",user,data:Data,credential:credentials[0]},null,2)+"\n");
        console.log("PASS: .NET-generated synthetic fixture captured from real Identity row and Chromium");
      }
      await page.getByRole("main").getByRole("button",{name:"Abmelden"}).click();
      await expect(page).toHaveURL(/\/anmelden\//);
      await page.getByRole("button",{name:"Mit Passkey anmelden"}).click();
      await expect(page).toHaveURL(/\/archiv\//);
      await expect(page.getByText("Angemeldet als Testmitglied (Member)")).toBeVisible();
      const me = await page.evaluate(async () => (await fetch("/api/auth/me")).json());
      assert.equal(me.authMethod,"passkey");
      assert.equal(errors.length,0,JSON.stringify(errors));
      console.log("PASS: unchanged frontend email → real enrollment → logout → discoverable passkey login");
    }
  } finally { await browser.close(); await db.end(); }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
