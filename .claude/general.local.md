# Repo-Specific Overrides

## GHCR Registry Owner

The GitHub user and GHCR registry owner for all rust repos is **`penguinztech`** (lowercase), NOT `penguintechinc`.

All container image references must use:
```
ghcr.io/penguinztech/<image>:<tag>
```

Never use `ghcr.io/penguintechinc/...` in this repo — that is incorrect and will cause image pull failures.

skill for oxide and/or rust plugins is in ./skills
