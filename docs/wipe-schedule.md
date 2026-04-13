# Wipe Schedule

The base image includes a built-in wipe watcher that deletes server save data on a configurable schedule and restarts the server cleanly. Blueprints are optionally wiped as well.

## Default behaviour

When `WIPE_SCHED` is unset (the default), the server wipes on the **first Thursday of every month** — the day Facepunch forces a wipe for all servers. This aligns your restart with the forced-wipe update so the new map is available the moment players reconnect.

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `WIPE_SCHED` | `""` (forced-only) | Wipe interval: `1w`, `2w`, `3w`, or `off` |
| `WIPE_DAY` | `Th` | Day of week: `M` `Tu` `W` `Th` `F` `Sa` `Su` |
| `WIPE_TIME` | `06:00` | UTC time the wipe actually happens (HH:MM). RCON warnings start 60 min earlier |
| `WIPE_BP` | `false` | Also wipe blueprints (`true`/`false`) |

**`WIPE_SCHED=off`** disables all automated wipes including the forced-wipe default.

## How WIPE_DAY works with WIPE_SCHED

`WIPE_DAY` is only used when `WIPE_SCHED` is set. It specifies which day of the week wipes happen:

```
WIPE_SCHED=1w  WIPE_DAY=Th   # Every Thursday  (weekly, Facepunch-day aligned)
WIPE_SCHED=2w  WIPE_DAY=Th   # Every other Thursday
WIPE_SCHED=3w  WIPE_DAY=Sa   # Every third Saturday
WIPE_SCHED=1w  WIPE_DAY=Sa   # Every Saturday
```

When `WIPE_SCHED` is unset (forced-only), `WIPE_DAY` has no effect — the watcher always targets the first Thursday of the month.

## Bi-weekly / tri-weekly interval alignment

For `2w` and `3w` intervals, the watcher uses the Unix epoch week number (`seconds / 604800`) modulo the interval to determine whether the current week is a wipe week. The Unix epoch started on a Thursday (Jan 1, 1970), so `2w` wipes fall on the same cadence globally — every server using `2w` will wipe on the same set of weeks.

## Blueprint wipes

By default only map data is wiped (players keep blueprints). Set `WIPE_BP=true` to also delete `player.blueprints.*.db`.

**First-Thursday wipes always wipe blueprints regardless of `WIPE_BP`**, because Facepunch's forced wipe typically requires a full wipe for content parity (new tier components, etc.).

## What gets deleted

| File pattern | What it is | Always deleted |
|---|---|---|
| `proceduralmap.*.map` | Map geometry | ✓ |
| `proceduralmap.*.sav` | World save (entities, loot) | ✓ |
| `proceduralmap.*.db` | Map database | ✓ |
| `player.blueprints.*.db` | Blueprint progress | Only when `WIPE_BP=true` or forced-wipe day |

Player data files (`player.deaths.*`, `player.identities.*`, etc.) are never deleted automatically.

## Wipe sequence

The watcher fires 60 minutes before `WIPE_TIME` and sends a full hour of RCON warnings so active players have time to finish runs and stash loot:

| T-minus | RCON broadcast |
|---|---|
| 60 min | `Scheduled server wipe in 60 minutes — plan accordingly!` |
| 50 min | `Server wipe in 50 minutes.` |
| 40 min | `Server wipe in 40 minutes.` |
| 30 min | `Server wipe in 30 minutes.` |
| 20 min | `Server wipe in 20 minutes.` |
| 10 min | `Server wipe in 10 minutes — wrap up your runs!` |
| 5 min  | `Server wipe in 5 minutes!` |
| 1 min  | `Server wipe in 60 seconds!` |
| 5 sec  | `Wiping now — see you on the new map!` |

Then: `server.save` → 3s wait → delete save files → write stamp file to PVC (prevents re-wipe on same day) → SIGTERM → container restart → fresh map.

The warning sequence covers both custom `WIPE_SCHED` wipes and the default first-Thursday Facepunch forced-wipe trigger.

## Stamp file

After a wipe, the date is written to `/steamcmd/rust/server/<identity>/.last-wipe` on the PVC. If the container restarts and comes back up on the same calendar day, the watcher skips the wipe — preventing a restart loop.

To force a re-wipe on the same day, delete the stamp file:

```bash
rm /steamcmd/rust/server/<identity>/.last-wipe
```

## K8s / Helm

Set `restartPolicy: Always` (default for Deployments). After the wipe SIGTERM, the pod exits 0 and Kubernetes restarts it with a clean map.

```yaml
env:
  - name: WIPE_SCHED
    value: "1w"
  - name: WIPE_DAY
    value: "Th"
  - name: WIPE_TIME
    value: "06:00"
  - name: WIPE_BP
    value: "false"
```
