# ARC-011-1 Passkey sign-in — handoff (ABGESCHLOSSEN)

**Stand:** 2026-09-18. Alle offenen Punkte sind erledigt; der Plan
[ARC-011-1-passkey-sign-in.md](ARC-011-1-passkey-sign-in.md) trägt den
Abschluss mit Verification-Abschnitt. Status: `done`.

## Abgeschlossen in dieser Session

1. Zeremonientest grün (`04d03fa`): vier echte Defekte behoben —
   doppel-codierte Options-JSON im Frontend, Dev-RP-ID-Fallback auf
   `localhost` (statt Request-Host), falsches Binding des
   `remove`-Endpoints, Hydration-Mismatch durch Render-Time
   `webAuthnSupported()`.
2. RP-ID/Origins in Bicep (`0944aa0`) — `Authentication__PasskeyRelyingPartyId`
   + `Authentication__PasskeyOrigins__0` aus `customDomain`, What-if
   Modify-only.
3. `--repair-admin` widerruft registrierte Passkeys (`4935069`) mit
   Backend-Tests für beide Reparatur-Pfade.
4. `MeResponse.authMethod` im Frontend (`c7b39e8`).
5. Plan-Status → done mit Verification-Abschnitt (`188908c`).

Backlog-Remainder (optional, aus dem Plan): AppHost-Integrationstest mit
echten Replikaten / Login über Replikate hinweg.

