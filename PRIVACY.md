# Privacy policy

Never commit character names, account identifiers, per-character settings, logs, screenshots,
memory captures, run reports, live-test evidence, baselines, crash dumps, or credentials.

Runtime and research data default to `%APPDATA%/BubblesBot` or
`%LOCALAPPDATA%/BubblesBot`, outside the repository. The character-selection visual oracle loads
its private identity profile from
`%LOCALAPPDATA%/BubblesBot/private/character-selection.json`.

Before committing, run:

```powershell
./eng/privacy-check.ps1 -IncludeUntracked
```

The same check runs in CI. This checkout also uses the tracked pre-commit hook under `.githooks`.
For a new clone, enable it once with:

```powershell
git config core.hooksPath .githooks
```

Do not bypass the guard with `git add -f`. If private data ever reaches a pushed commit, deleting
it in a later commit is insufficient; the Git history and remote refs must be rewritten.
