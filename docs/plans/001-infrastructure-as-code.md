# Infrastructure as Code Plan

## Goal

Bring the Azure estate for Liedertafel under Infrastructure as Code using **Bicep**
and auto-deploy infrastructure changes through **GitHub Actions**. Only changes to
`infrastructure/**` trigger the infra deployment; the existing app build-and-deploy
workflow stays untouched.

## Current Azure estate (resource group `RG-Liedertafel`)

| Resource               | Name                      | Notes                                                                                   |
| ---------------------- | ------------------------- | --------------------------------------------------------------------------------------- |
| Static Web App         | `liedertafel`             | Free, `westeurope`; custom domains `liedertafel-mining.at` + `www.liedertafel-mining.at` |

There are **no** other resources in the subscription tied to Liedertafel: no storage
account, no API, no app settings, no Key Vault, no budgets, no subscription-level
resources. This makes the adoption significantly simpler than the Alpakasoelde estate.

Deployment today happens via the GitHub-integration workflow
(`.github/workflows/build-and-deploy.yml` using `Azure/static-web-apps-deploy`). There
is no IaC in the repo yet.

## Approach: adopt existing resources in place

Bicep declares the Static Web App with its existing name, resource group, location,
and SKU, so the first deployment is an idempotent adopt with no recreation or
downtime. `what-if` is used to verify this before applying.

## Milestones (tracked)

Checkboxes are updated as work progresses.

- [x] Create git branch `feat/infrastructure-as-code`
- [x] Write infrastructure plan (`docs/plans/001-infrastructure-as-code.md`)
- [x] Finalize plan decisions (service principal, custom-domain gating)
- [x] Scaffold Bicep templates (`main.bicep`, module `static-sites.bicep`, `*.bicepparam`, `bicepconfig.json`)
- [x] Validate templates locally (`az bicep build` + `az deployment group what-if` — only `Modify`, no `Delete`/`Replace`)
- [ ] Write `.github/workflows/infra-deploy.yml` (`what-if` PR job + deploy on main)
- [ ] Manual: create dedicated service principal + OIDC federated credentials, add
      GitHub secrets (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`)
- [ ] First deploy: review `what-if` → apply → verify the site stays live
- [ ] Update `AGENTS.md` and README (add IaC section and deployment commands)

## 1. Bicep structure under `infrastructure/`

```
infrastructure/
  main.bicep              # RG-scoped orchestrator (targetScope = resourceGroup)
  main.bicepparam         # values: RG name, location, resource name, custom domains
  bicepconfig.json        # lint rules (mirrors the Alpakasoelde repo)
  modules/
    static-sites.bicep    # the Static Web App + customDomains (adopt in place)
```

Details:

- `main.bicep` declares only the Static Web App `liedertafel` (Free, `westeurope`)
  and the two custom domains (`liedertafel-mining.at`, `www.liedertafel-mining.at`)
  as `staticSites/customDomains` child resources.
- The GitHub-integration properties (`repositoryUrl`, `branch`, build properties)
  are **not** declared; they stay managed by the existing GitHub-integration workflow.
- Custom domains are declared in Bicep but gated on a `what-if` review first: if
  `what-if` reports a destructive `Replace`, they are left out of IaC initially and
  stay portal-managed (they are already configured and verified).
- No Key Vault, no seed script, no `main-subscription.bicep`: there are no app
  settings, secrets, or subscription-level resources to adopt.

## 2. Secret management

Not applicable. The Static Web App has no app settings and no linked resources, so no
secrets exist and no Key Vault is introduced.

## 3. GitHub Actions – infra auto-deploy

New workflow `.github/workflows/infra-deploy.yml`, modelled on the Alpakasoelde repo's
`infra-deploy.yml`:

- **Triggers:** `push` to `main` with paths `infrastructure/**` and
  `.github/workflows/infra-deploy.yml`, `pull_request` for a `what-if` preview job,
  and `workflow_dispatch` for both.
- **Deploy identity (one-time setup):**
  1. Create a dedicated service principal `sp-liedertafel-iac` for this repo.
  2. Add two OIDC federated credentials for this GitHub repository
     (`repo:JakobFerdinand/liedertafel:ref:refs/heads/main` and
     `repo:JakobFerdinand/liedertafel:pull_request`).
  3. Grant `Contributor` on `RG-Liedertafel` only.
  4. Store `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` in repo
     secrets.
- **Jobs:**
  - `what-if` (pull requests + dispatch): `Azure/login@v3` → `az bicep build` →
    `az deployment group what-if` → post the diff as a PR comment (marker
    `<!-- infra-what-if -->`), so infra PRs show their impact before merge.
  - `deploy` (main + dispatch): `Azure/login@v3` → `az bicep build` →
    `az deployment group what-if` → guard: abort if any `Delete`/`Replace` change →
    `az deployment group create`.
  - The existing `build-and-deploy.yml` (app deployment) stays untouched.

## 4. Rollout order (safe, no downtime)

1. **One-time prep:** create the dedicated service principal + OIDC federated
   credentials, grant `Contributor` on `RG-Liedertafel`, add the three GitHub
   secrets.
2. Commit the Bicep templates plus `infra-deploy.yml`; run a `workflow_dispatch`-ed
   `what-if` to confirm no destructive changes on the Static Web App and the custom
   domains (the adoption hotspot).
3. Deploy; verify the site stays live and both custom domains still resolve.
4. Update `AGENTS.md` (add IaC section with `az deployment group …` commands) and
   README.

## 5. Known limitations (kept manual, documented)

- Custom domain DNS records already exist and are verified; Bicep cannot create or
  verify DNS records. If `what-if` reports a destructive `Replace` for
  `staticSites/customDomains`, they stay portal-managed.
- The GitHub integration properties of the Static Web App (repo, branch, build
  config) are portal/GitHub-managed and not declared in Bicep.

## Decisions

- **Bicep**, RG-scoped `main.bicep` with a `static-sites.bicep` module, adopting the
  existing Static Web App in place.
- **No Key Vault / no secrets:** the SWA has no app settings; nothing to migrate.
- **GitHub Actions:** dedicated service principal `sp-liedertafel-iac` (least
  privilege, isolated from the Alpakasoelde identity) with OIDC federated
  credentials scoped to this repo.
- **Custom domains:** declared as child resources, gated on `what-if` review; fall
  back to portal-managed if `Replace` is reported.
- **Pull requests:** infra changes run a `what-if` job that posts the diff as a PR
  comment.