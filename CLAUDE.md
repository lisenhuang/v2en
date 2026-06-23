# Project Guidelines

## Git

- **Do NOT run `git commit` automatically.** Committing is the human's job.
- You may stage changes, write code, and prepare a commit message when asked,
  but the actual `git commit` (and `git push`) is performed manually by the human.
- Never commit or push unless the human explicitly asks you to in that moment.
  Approval to commit once does not carry over to future changes.

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
