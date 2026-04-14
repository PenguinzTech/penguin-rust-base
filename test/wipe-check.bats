#!/usr/bin/env bats
# Tests for docker/wipe-check.sh wipe executor.

load 'test_helper/bats-support/load'
load 'test_helper/bats-assert/load'
load 'test_helper/setup_helper'

# wipe-check.sh sources lib-functions.sh from /usr/local/lib/penguin-rust/.
# For testing, we create a wrapper that overrides the source path.
run_wipe_check() {
    # Create a modified version that sources from our docker dir
    local wrapper="${TEST_TMPDIR}/wipe-check-wrapper.sh"
    sed "s|. /usr/local/lib/penguin-rust/lib-functions.sh|. ${DOCKER_DIR}/lib-functions.sh|" \
        "${DOCKER_DIR}/wipe-check.sh" > "${wrapper}"
    chmod +x "${wrapper}"

    # Mock sleep to be instant (the 60-min countdown would take forever)
    cat > "${TEST_TMPDIR}/mock-bin/sleep" <<'MOCK'
#!/bin/bash
exit 0
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/sleep"

    # Mock kill to be no-op
    cat > "${TEST_TMPDIR}/mock-bin/kill" <<'MOCK'
#!/bin/bash
exit 0
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/kill"

    # Mock date unless overridden by test
    if [ ! -f "${TEST_TMPDIR}/mock-bin/date" ]; then
        cat > "${TEST_TMPDIR}/mock-bin/date" <<'MOCK'
#!/bin/bash
# Default: Thursday, first week of month
for arg in "$@"; do
    case "${arg}" in
        +%Y-%m-%d) echo "2026-01-01"; exit 0 ;;
        +%u)       echo "4"; exit 0 ;;        # Thursday
        +%-d)      echo "1"; exit 0 ;;        # 1st of month
        +%s)       echo "1735689600"; exit 0 ;; # epoch
    esac
done
# Fallback for other date calls
/usr/bin/date "$@"
MOCK
        chmod +x "${TEST_TMPDIR}/mock-bin/date"
    fi

    # Create required server directories
    local server_dir="/steamcmd/rust/server/${RUST_SERVER_IDENTITY:-test_server}"
    mkdir -p "${TEST_TMPDIR}/server-dir"

    # Override paths in the wrapper
    sed -i "s|/steamcmd/rust/server/\${_W_IDENT}|${TEST_TMPDIR}/server-dir|g" "${wrapper}"

    bash "${wrapper}"
}

# ─── Guards ─────────────────────────────────────────────────────────────────

@test "wipe-check: exits when server not running" {
    # Mock pgrep to fail (server not running)
    cat > "${TEST_TMPDIR}/mock-bin/pgrep" <<'MOCK'
#!/bin/bash
exit 1
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/pgrep"

    run run_wipe_check
    assert_success
    refute_output --partial "Wipe triggered"
}

@test "wipe-check: exits when already wiped today (stamp file)" {
    # Create stamp file with today's date
    mkdir -p "${TEST_TMPDIR}/server-dir"
    echo "2026-01-01" > "${TEST_TMPDIR}/server-dir/.last-wipe"

    run run_wipe_check
    assert_success
    refute_output --partial "Wipe triggered"
}

@test "wipe-check: exits when not first week and no WIPE_SCHED" {
    export WIPE_SCHED=""

    # Override date to return dom=15 (not first week)
    cat > "${TEST_TMPDIR}/mock-bin/date" <<'MOCK'
#!/bin/bash
for arg in "$@"; do
    case "${arg}" in
        +%Y-%m-%d) echo "2026-01-15"; exit 0 ;;
        +%u)       echo "4"; exit 0 ;;
        +%-d)      echo "15"; exit 0 ;;
        +%s)       echo "1736899200"; exit 0 ;;
    esac
done
/usr/bin/date "$@"
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/date"

    run run_wipe_check
    assert_success
    assert_output --partial "Not first week"
}

@test "wipe-check: runs wipe on first Thursday of month" {
    export WIPE_SCHED=""

    run run_wipe_check
    assert_success
    assert_output --partial "Wipe triggered"
}

# ─── 2w/3w interval check ──────────────────────────────────────────────────

@test "wipe-check: 2w schedule skips non-matching week" {
    export WIPE_SCHED="2w"

    # epoch 1736380800 / 604800 = 2871 (odd) → 2871 % 2 = 1 → skip
    cat > "${TEST_TMPDIR}/mock-bin/date" <<'MOCK'
#!/bin/bash
for arg in "$@"; do
    case "${arg}" in
        +%Y-%m-%d) echo "2026-01-08"; exit 0 ;;
        +%u)       echo "4"; exit 0 ;;
        +%-d)      echo "8"; exit 0 ;;
        +%s)       echo "1736380800"; exit 0 ;;
    esac
done
/usr/bin/date "$@"
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/date"

    run run_wipe_check
    assert_success
    assert_output --partial "Not a 2w week"
}

@test "wipe-check: 2w schedule runs on matching week" {
    export WIPE_SCHED="2w"

    # epoch_weeks must be even: 2870 * 604800 = 1735977600
    cat > "${TEST_TMPDIR}/mock-bin/date" <<'MOCK'
#!/bin/bash
for arg in "$@"; do
    case "${arg}" in
        +%Y-%m-%d) echo "2026-01-04"; exit 0 ;;
        +%u)       echo "4"; exit 0 ;;
        +%-d)      echo "4"; exit 0 ;;
        +%s)       echo "1735977600"; exit 0 ;;  # 1735977600/604800=2870 even
    esac
done
/usr/bin/date "$@"
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/date"

    run run_wipe_check
    assert_success
    assert_output --partial "Wipe triggered"
}

# ─── Blueprint wipe logic ──────────────────────────────────────────────────

@test "wipe-check: forces blueprint wipe on first Thursday" {
    export WIPE_BP="false"

    # date mock: first Thursday (dow=4, dom=1)
    run run_wipe_check
    assert_success
    assert_output --partial "blueprint_wipe=true"
}

@test "wipe-check: respects WIPE_BP=true" {
    export WIPE_BP="true"
    export WIPE_SCHED="1w"

    # Non-first-Thursday
    cat > "${TEST_TMPDIR}/mock-bin/date" <<'MOCK'
#!/bin/bash
for arg in "$@"; do
    case "${arg}" in
        +%Y-%m-%d) echo "2026-01-15"; exit 0 ;;
        +%u)       echo "4"; exit 0 ;;
        +%-d)      echo "15"; exit 0 ;;
        +%s)       echo "1736899200"; exit 0 ;;  # 1736899200/604800=2871 → odd → might skip
    esac
done
/usr/bin/date "$@"
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/date"

    # 1w doesn't check interval modulo, but we set WIPE_SCHED=1w which doesn't hit 2w/3w case
    run run_wipe_check
    assert_success
    assert_output --partial "blueprint_wipe=true"
}

# ─── Stamp file creation ───────────────────────────────────────────────────

@test "wipe-check: creates stamp file after wipe" {
    run run_wipe_check
    assert_success
    assert [ -f "${TEST_TMPDIR}/server-dir/.last-wipe" ]
    run cat "${TEST_TMPDIR}/server-dir/.last-wipe"
    assert_output "2026-01-01"
}

# ─── Map file deletion ─────────────────────────────────────────────────────

@test "wipe-check: deletes map files" {
    # Create fake map files (mkdir first — run_wipe_check overrides path)
    mkdir -p "${TEST_TMPDIR}/server-dir"
    touch "${TEST_TMPDIR}/server-dir/proceduralmap.1234.map"
    touch "${TEST_TMPDIR}/server-dir/proceduralmap.1234.sav"
    touch "${TEST_TMPDIR}/server-dir/proceduralmap.1234.db"

    run run_wipe_check
    assert_success
    assert [ ! -f "${TEST_TMPDIR}/server-dir/proceduralmap.1234.map" ]
    assert [ ! -f "${TEST_TMPDIR}/server-dir/proceduralmap.1234.sav" ]
    assert [ ! -f "${TEST_TMPDIR}/server-dir/proceduralmap.1234.db" ]
}

@test "wipe-check: deletes blueprints when bp=true" {
    export WIPE_BP="false"
    # First Thursday triggers bp=true regardless of WIPE_BP
    mkdir -p "${TEST_TMPDIR}/server-dir"
    touch "${TEST_TMPDIR}/server-dir/player.blueprints.1234.db"

    run run_wipe_check
    assert_success
    assert_output --partial "Wiping blueprints"
    assert [ ! -f "${TEST_TMPDIR}/server-dir/player.blueprints.1234.db" ]
}

# ─── RCON warnings ──────────────────────────────────────────────────────────

@test "wipe-check: sends RCON warnings during countdown" {
    run run_wipe_check
    assert_success
    assert [ -f "${RCON_LOG}" ]

    run cat "${RCON_LOG}"
    assert_output --partial "Scheduled server wipe in 60 minutes"
    assert_output --partial "Server wipe in 50 minutes"
    assert_output --partial "Server wipe in 10 minutes"
    assert_output --partial "Wiping now"
    assert_output --partial "server.save"
}
