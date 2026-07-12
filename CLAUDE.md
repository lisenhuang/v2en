# Project Guidelines

## Version stamp — bump it on every code change

The home page shows a build/version stamp from `AppVersion.Stamp` (in `AppVersion.cs`),
formatted `yy.MM.dd.HH:mm` in **UTC (timezone 0)**, e.g. `26.06.26.09:47`.

- **Every time you modify code, update `AppVersion.Stamp` to the current real UTC time.**
- **Get the real time from the internet — do NOT guess or use the local clock.** A reliable
  way is to read an HTTP `Date:` header (it's GMT/UTC), e.g.
  `curl -sI https://www.google.com | grep -i '^date:'`, then format it as `yy.MM.dd.HH:mm`.
- This is part of finishing a change, like building — do it before you hand off.

## Git

- **Do NOT run `git commit` automatically.** Committing is the human's job.
- You may stage changes, write code, and prepare a commit message when asked,
  but the actual `git commit` (and `git push`) is performed manually by the human.
- Never commit or push unless the human explicitly asks you to in that moment.
  Approval to commit once does not carry over to future changes.
- **When the AI does make a git commit (once the human has asked for it), always
  commit as the git user `Ethan <lisen8018@gmail.com>`** — set
  `git -c user.name="Ethan" -c user.email="lisen8018@gmail.com" commit ...` (or the
  equivalent `user.name`/`user.email` config) so the commit is authored by that
  identity, never any other.
- **After committing, push to the `main` branch directly.**

## Never break the published website — changes must be backward-compatible

The site is **already deployed and running on real data**. Every code and DB-structure
change must upgrade the live deployment cleanly, without data loss or downtime, and
must keep working against data created by older versions. Treat this as a hard
constraint, not a nice-to-have.

- **Additive, not destructive.** Prefer adding new tables/columns/settings over
  renaming or removing existing ones. Do not drop or rename a column, change its type,
  or repurpose its meaning if old rows still rely on it. If a removal is truly
  necessary, deprecate first (stop writing, keep reading) and remove only in a later,
  separate step.
- **Old data must still load.** New columns must be nullable or have sensible
  defaults so existing rows remain valid. Never write a migration that fails (or
  silently corrupts data) when run against the current production database.
- **Forward-only, auto-applied migrations.** Migrations run automatically at prod
  startup via `db.Database.Migrate()`, so each migration must apply on top of real
  existing data and must not require manual fix-up. No destructive `DROP`/`DELETE`
  unless I explicitly approve it for that change.
- **New config/settings must default safely.** Any new `appsettings.json` key, env
  var, or runtime setting must have a default that preserves current behavior when
  it's absent, so the deployed site keeps working before I set anything.
- **Don't break existing URLs/APIs/contracts.** Keep existing routes, query params,
  page names, and serialized formats working, or provide a compatible fallback.
- **Call out any compatibility risk.** If a change *could* affect old data or the
  live site, say so explicitly in the hand-off and describe the safe upgrade path —
  don't assume it's fine.

## After coding — always hand off with "what to do next"

Every time you finish a coding change, **first build it and fix what's broken**,
then hand off:

1. **Build and fix** — run `dotnet build` and fix every compile error before you
   report back. Never hand me code that doesn't compile. Mention any remaining
   warnings, but pre-existing ones (e.g. the SQLitePCLRaw advisory) needn't block.
   If a build is genuinely not possible, say so and why.
2. End your reply with a short **Next steps** section telling me exactly what I need
   to do to run/verify it. Cover, when relevant:

- **DB migrations** — do NOT infer this from the code diff alone; actively **check
  the real migration state** every time, because I may have forgotten to migrate an
  earlier change. Run:
    - `dotnet ef migrations has-pending-model-changes` — is the EF model ahead of the
      latest migration? If yes, create one: `dotnet ef migrations add <Name>`.
    - `dotnet ef migrations list` — are there migrations not yet applied to the DB
      (marked `(Pending)`)? If yes, apply them: `dotnet ef database update`.
  Report what the check actually found. The Docker/prod path applies pending
  migrations automatically at startup via `db.Database.Migrate()`, so there the main
  thing is whether a *new migration* must be added. Only say "no DB migration needed"
  **after** the check confirms the model and the latest migration are in sync.
- **Rebuild / restart** — whether I need to `dotnet build`, restart `dotnet run`,
  or rebuild the container (`docker compose up -d --build`) for the change to take
  effect (e.g. config in `appsettings.json` is only read at startup).
- **Config / secrets** — any new setting, env var, or `dotnet user-secrets` value I
  must set.
- **Verify** — the quickest way to confirm it works (a URL to open, a `curl`, etc.).
- **Commit** — a one-line reminder that committing/pushing is mine to do (per the
  Git rules above), with the suggested message if you prepared one.

Keep it concise and specific to what actually changed — skip items that don't apply.

3. **Always end with a one- or two-line TL;DR** answering, explicitly:
   - **Safe to publish?** — is it safe to deploy over the live site and does it stay
     compatible with the old website / old data (per the backward-compatibility rule
     above)? Say "✅ safe to publish" or call out exactly what's risky.
   - **Migrate first?** — do I need to run a DB migration before/at deploy, or not?
     State it plainly: e.g. "⚠️ run `dotnet ef database update` first" or "no DB
     migration needed (checked)". Base this on the actual migration-state check, not
     a guess.
