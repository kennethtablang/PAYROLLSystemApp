# Revision and release workflow

How a request from the company in Manila becomes an installed update, with the
developer working remotely (Pangasinan). The company only does two things: it
**sends request files** and it **clicks "Back up and install"**.

```
 MANILA                                   PANGASINAN (developer)
 ──────                                   ──────────────────────
 Help & Updates → Save request file
   REQ-261006-1430.zip  ── Messenger ──▶  1. Log it in docs/REVISIONS.md
                                          2. Reproduce, fix, test
                                          3. CHANGELOG.md  →  build-installer -Version x.y
                                          4. Test the upgrade on your own PC
                                          5. Commit, push  →  publish-release
 "Update x.y available" ◀── GitHub Releases ──┘
 Back up and install
 (backup → install → reopen)
```

## 1. Receive

- Requests arrive as `REQ-yyMMdd-HHmm.zip`. Each contains `REQUEST.txt`
  (version, screen, Windows, role), any screenshots under `attachments/`, and
  sometimes `database/payroll_….db3`.
- Save them to a private folder **outside the repo**, for example
  `Documents\Payroll MS clients\Manila\requests\`. Never commit them, and never
  upload a database copy anywhere.
- Add a row to [`docs/REVISIONS.md`](REVISIONS.md) the same day, then reply to
  the company with the reference and status (Accepted / Need info / Declined).
- Anything that changes the money (rates, premiums, statutory tables, rounding)
  needs a worked example with real figures from the company **before** you
  start. Write the agreed example into the request notes.

## 2. Plan the version

| Kind | Version | When |
|---|---|---|
| Urgent problem (payroll cannot be finished) | patch `1.1.1` | Same or next day, nothing else included |
| Normal problems and changes | minor `1.2` | Batched; aim for one release per cut-off, published **just after** a payroll is posted, never during one |
| Big changes (new module, new report layout) | minor, agreed scope first | Confirm scope and cost in writing before work starts |

Avoid publishing between a payroll calculation and its posting. An update
mid-cut-off means the figures were computed by two versions.

## 3. Reproduce and fix

- If a database copy was sent, reproduce it on your own PC: copy the `.db3`
  into `Documents\Payroll MS\Backups\` and use **Backup & Archive → Restore**.
  (Take your own backup first.) Delete the client's copy when the request is
  done.
- Work on a branch per request when it is more than a one-line fix:
  `git switch -c req/REQ-261006-1430`.
- Put the reference in the commit message: `Fix NSD past midnight (REQ-261006-1430)`.
- **Database changes must stay additive**: new tables and new columns only.
  The updater installs over the old version and keeps the same `payroll.db3`,
  and the company may have to restore a backup taken by the previous version.
  A rename or a dropped column needs a migration in `PayrollDatabase` (see
  `MigrateDetachmentRatesAsync`) and a note in the changelog.

## 4. Test

1. Build and run in Visual Studio and go through the screen the request was about.
2. For anything touching pay: run one full cut-off (timesheets → calculate →
   approve → post → payslip → payroll summary). Compare it with the worked
   example from step 1.
3. Run the regression harness if you have it (see "Known gaps" below).

## 5. Release

```powershell
# 1. Write the "## 1.2" section in CHANGELOG.md, in plain words for payroll
#    staff, naming each REQ- it answers.

# 2. Build: sets the version, publishes, compiles the installer and its .sha256
.\build-installer.ps1 -Version 1.2

# 3. Test the upgrade: with the previous version installed on this PC, run the
#    new PayrollSystemSetup-1.2.exe. Check it opens on the same data.

# 4. Commit and push (the installer travels via Git LFS)
git add -A
git commit -m "Release 1.2"
git push

# 5. Publish. -Draft first if you want to look at the release page before
#    the company's PC can see it.
.\publish-release.ps1            # or: .\publish-release.ps1 -Draft
```

One-time setup for step 5: `winget install --id GitHub.cli -e`, then `gh auth login`.

### The release signing key (keep it safe)

`build-installer.ps1` signs every release with the private key at
`%APPDATA%\PayrollMS-Release\signing-key.pem`. It was created on 2026-10-06 by
`dotnet run tools/release-signing.cs -- newkey`.

- **Back it up now** to somewhere private, like a USB drive or a password
  manager. **Never** commit it or send it to anyone.
- **New PC:** copy the file to the same path.
- **Lost or leaked key:** run `newkey` again, paste the new public key into
  `UpdateService.ReleasePublicKey`, build, and send that installer to the
  company to install **by hand** once. Copies with the old key will refuse
  releases signed with the new one.

Then message the company: "Version 1.2 is ready. An administrator can install
it from the button at the top after signing in."

Finally, set each answered request in `REVISIONS.md` to `Done in 1.2`.

## 6. If an update goes wrong

- **At the company:** Backup & Archive → Restore the backup taken just before the
  update (the newest one), then send a request file.
- **You:** fix forward. Publish `1.2.1` with the fix. The updater only offers
  versions newer than the installed one, so the company cannot be moved back
  through the app. If you must go back, send them the older installer to run by
  hand. Inno Setup will install it over the newer one.
- A release that should never be installed: delete it on GitHub (or turn it
  back into a draft). The app stops offering it at the next sign-in.

## How the updater works (for maintenance)

- `Services/UpdateService.cs` reads
  `https://api.github.com/repos/kennethtablang/PAYROLLSystemApp/releases/latest`.
  Drafts and pre-releases are ignored. The tag (`v1.2`) is compared with the
  running version, which comes from `ApplicationDisplayVersion` in the csproj.
- The release must carry `PayrollSystemSetup-x.y.exe`, its `.sha256`, **and**
  `.sha256.sig`. The app checks the signature against the public key built
  into `UpdateService.ReleasePublicKey`, then checks the installer against the
  checksum. If either check fails, nothing runs. Download links are accepted
  only over `https` from `github.com`.
- Why the signature: the checksum sits in the same release as the installer,
  so it only proves the download was not damaged. The signature proves **you**
  built it, so someone who got into the GitHub account could not push a
  program onto the company's PC.
- The app downloads with `HttpClient`, so the file has no "downloaded from
  the internet" mark (Mark of the Web). That mark is what triggers SmartScreen's
  "Windows protected your PC". The checksum is what makes skipping that
  warning safe.
- Installing: database backup → audit entry `UPDATE_STARTED` → installer runs
  `/SILENT` → app quits → installer reopens it (`[Run]` entry with
  `Check: WizardSilent` in `installer/PayrollSystem.iss`).
- Only an administrator (`Permission.ManageBackups`) can install, because
  installing takes a backup first.
- The repository must stay **public** for this to work without a token.

## SmartScreen: the long-term fix

The first install of each new PC still shows "Windows protected your PC",
because the installer is not code-signed. The way to remove it entirely is
code signing. Check current prices and eligibility before buying:

- **Azure Artifact Signing** (formerly Trusted Signing): a low monthly fee and
  works with `signtool`. Eligibility for individual developers outside the US
  and Canada has been limited, so check that it accepts you first.
- **A code-signing certificate** from a certificate authority (Sectigo,
  DigiCert, SSL.com and others): yearly cost, delivered on a hardware token or
  cloud HSM.

Since 2024 even signed files build SmartScreen reputation gradually rather
than instantly. Signing mostly makes the warning name a publisher, and helps
with Smart App Control. For one company that installs once, "More info → Run
anyway" plus the updater is enough. Revisit signing if the app goes to more
clients.

## Known gaps

- **The regression harness is not in the repository.** The 341-check
  end-to-end harness from 2026-09-22 was built in a temporary folder, and only
  its compiled output remains. Rebuilding it as `tests/` in this repo should
  be the next piece of work. Without it, every release depends on manual
  testing.
- **Git LFS quota.** Each installer (~100 MB) committed through LFS counts
  against GitHub's free LFS storage and bandwidth (about 1 GB each). Now that
  installers are attached to GitHub Releases, which have no such quota,
  consider no longer committing them. Point `OutputDir` in the `.iss` back to
  `Export\`.
