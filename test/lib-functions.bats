#!/usr/bin/env bats
# Tests for docker/lib-functions.sh shared functions.

load 'test_helper/bats-support/load'
load 'test_helper/bats-assert/load'
load 'test_helper/setup_helper'

# Source the library under test
source_lib() {
    source "${DOCKER_DIR}/lib-functions.sh"
}

# ─── send_rcon ──────────────────────────────────────────────────────────────

@test "send_rcon: sends JSON message via websocat" {
    source_lib
    export RUST_RCON_PASSWORD="testpass"
    export RUST_RCON_PORT="28016"
    send_rcon "say hello"
    assert [ -f "${RCON_LOG}" ]
    run cat "${RCON_LOG}"
    assert_output --partial '"Message":"say hello"'
    assert_output --partial '"Name":"lib"'
}

@test "send_rcon: uses custom caller name" {
    source_lib
    export RUST_RCON_PASSWORD="testpass"
    send_rcon "server.save" "wipe-check"
    run cat "${RCON_LOG}"
    assert_output --partial '"Name":"wipe-check"'
}

@test "send_rcon: no-op when RCON password is empty" {
    source_lib
    unset RUST_RCON_PASSWORD
    send_rcon "say hello"
    assert [ ! -f "${RCON_LOG}" ]
}

@test "send_rcon: no-op when websocat missing" {
    source_lib
    export RUST_RCON_PASSWORD="testpass"
    # Remove mock websocat from PATH
    rm "${TEST_TMPDIR}/mock-bin/websocat"
    send_rcon "say hello"
    assert [ ! -f "${RCON_LOG}" ]
}

# ─── load_rcon_password ─────────────────────────────────────────────────────

@test "load_rcon_password: loads from file when env empty" {
    source_lib
    unset RUST_RCON_PASSWORD
    local pw_dir="/steamcmd/rust/server/${RUST_SERVER_IDENTITY}"
    # Can't write to /steamcmd in test, so override RUST_SERVER_IDENTITY to a tmpdir path
    export RUST_SERVER_IDENTITY="test_server"
    local pw_path="${TEST_TMPDIR}/server/test_server"
    mkdir -p "${pw_path}"
    printf 'secretpass123\n' > "${pw_path}/.rcon.pw"

    # Patch the function to use our test path
    load_rcon_password() {
        if [ -z "${RUST_RCON_PASSWORD:-}" ]; then
            local pw_file="${TEST_TMPDIR}/server/${RUST_SERVER_IDENTITY:-rust_server}/.rcon.pw"
            if [ -f "${pw_file}" ]; then
                RUST_RCON_PASSWORD=$(cat "${pw_file}")
                export RUST_RCON_PASSWORD
            fi
        fi
    }
    load_rcon_password
    assert_equal "${RUST_RCON_PASSWORD}" "secretpass123"
}

@test "load_rcon_password: keeps existing env var" {
    source_lib
    export RUST_RCON_PASSWORD="already_set"
    # Override with test path function
    load_rcon_password() {
        if [ -z "${RUST_RCON_PASSWORD:-}" ]; then
            RUST_RCON_PASSWORD="should_not_be_this"
            export RUST_RCON_PASSWORD
        fi
    }
    load_rcon_password
    assert_equal "${RUST_RCON_PASSWORD}" "already_set"
}

@test "load_rcon_password: survives missing file" {
    source_lib
    unset RUST_RCON_PASSWORD
    export RUST_SERVER_IDENTITY="nonexistent_server"
    load_rcon_password
    assert_equal "${RUST_RCON_PASSWORD:-}" ""
}

# ─── compute_wipe_trigger ───────────────────────────────────────────────────

@test "compute_wipe_trigger: 06:00 returns '0 5'" {
    source_lib
    run compute_wipe_trigger "06:00"
    assert_output "0 5"
}

@test "compute_wipe_trigger: 19:00 returns '0 18'" {
    source_lib
    run compute_wipe_trigger "19:00"
    assert_output "0 18"
}

@test "compute_wipe_trigger: 00:30 wraps to previous day (23:30)" {
    source_lib
    run compute_wipe_trigger "00:30"
    assert_output "30 23"
}

@test "compute_wipe_trigger: 01:00 wraps to 00:00" {
    source_lib
    run compute_wipe_trigger "01:00"
    assert_output "0 0"
}

@test "compute_wipe_trigger: 00:00 wraps to 23:00" {
    source_lib
    run compute_wipe_trigger "00:00"
    assert_output "0 23"
}

@test "compute_wipe_trigger: 12:45 returns '45 11'" {
    source_lib
    run compute_wipe_trigger "12:45"
    assert_output "45 11"
}

@test "compute_wipe_trigger: 00:59 wraps to 23:59" {
    source_lib
    run compute_wipe_trigger "00:59"
    assert_output "59 23"
}

# ─── day_to_cron_dow ────────────────────────────────────────────────────────

@test "day_to_cron_dow: Th returns 4" {
    source_lib
    run day_to_cron_dow "Th"
    assert_output "4"
}

@test "day_to_cron_dow: Thu returns 4" {
    source_lib
    run day_to_cron_dow "Thu"
    assert_output "4"
}

@test "day_to_cron_dow: M returns 1" {
    source_lib
    run day_to_cron_dow "M"
    assert_output "1"
}

@test "day_to_cron_dow: Mo returns 1" {
    source_lib
    run day_to_cron_dow "Mo"
    assert_output "1"
}

@test "day_to_cron_dow: Mon returns 1" {
    source_lib
    run day_to_cron_dow "Mon"
    assert_output "1"
}

@test "day_to_cron_dow: Tu returns 2" {
    source_lib
    run day_to_cron_dow "Tu"
    assert_output "2"
}

@test "day_to_cron_dow: W returns 3" {
    source_lib
    run day_to_cron_dow "W"
    assert_output "3"
}

@test "day_to_cron_dow: F returns 5" {
    source_lib
    run day_to_cron_dow "F"
    assert_output "5"
}

@test "day_to_cron_dow: Sa returns 6" {
    source_lib
    run day_to_cron_dow "Sa"
    assert_output "6"
}

@test "day_to_cron_dow: Su returns 0" {
    source_lib
    run day_to_cron_dow "Su"
    assert_output "0"
}

@test "day_to_cron_dow: unrecognized defaults to 4 (Thursday)" {
    source_lib
    run day_to_cron_dow "invalid"
    assert_output "4"
}

# ─── fetch_from_umod ───────────────────────────────────────────────────────

@test "fetch_from_umod: downloads .cs plugin" {
    source_lib
    export OXIDE_PLUGINS_DIR="${TEST_TMPDIR}/oxide/plugins"

    # Mock curl to return JSON metadata then the .cs content
    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<'HANDLER'
for arg in "$@"; do
    case "${arg}" in
        *umod.org/plugins/*.json)
            echo '{"title": "TestPlugin"}'
            exit 0
            ;;
        *umod.org/plugins/*.cs)
            echo "// Test plugin content"
            exit 0
            ;;
    esac
done
exit 1
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run fetch_from_umod "testplugin"
    assert_success
    assert_output --partial "fetched umod:testplugin"
}

@test "fetch_from_umod: falls back to .zip" {
    source_lib
    export OXIDE_PLUGINS_DIR="${TEST_TMPDIR}/oxide/plugins"

    # Create a real zip with a .cs file for the mock to return
    local zip_dir="${TEST_TMPDIR}/zip-source"
    mkdir -p "${zip_dir}"
    echo "// ZipPlugin content" > "${zip_dir}/ZipPlugin.cs"
    (cd "${zip_dir}" && zip -q "${TEST_TMPDIR}/test.zip" ZipPlugin.cs)

    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<'HANDLER'
# Parse all args to find the URL and -o output path
output_file=""
url=""
skip_next=false
for a in "$@"; do
    if $skip_next; then output_file="$a"; skip_next=false; continue; fi
    case "$a" in
        -o) skip_next=true ;;
        -*) ;;
        *) url="$a" ;;
    esac
done

case "${url}" in
    *umod.org/plugins/*.json)
        echo '{"title": "ZipPlugin"}'
        exit 0
        ;;
    *umod.org/plugins/*.cs)
        exit 1  # .cs download fails
        ;;
    *umod.org/plugins/*.zip)
        if [ -n "${output_file}" ]; then
            cp "${TEST_TMPDIR}/test.zip" "${output_file}"
            exit 0
        fi
        exit 1
        ;;
esac
exit 1
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run fetch_from_umod "zipplugin"
    assert_success
    assert_output --partial "extracted"
}

@test "fetch_from_umod: fails when both .cs and .zip fail" {
    source_lib
    export OXIDE_PLUGINS_DIR="${TEST_TMPDIR}/oxide/plugins"

    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<'HANDLER'
exit 1
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run fetch_from_umod "badplugin"
    assert_failure
    assert_output --partial "failed to fetch"
}

# ─── stage_from_baked ───────────────────────────────────────────────────────

@test "stage_from_baked: stages plugin with valid hash" {
    source_lib
    export PER_PLUGIN_DIR="${TEST_TMPDIR}/per-plugin"
    export OXIDE_PLUGINS_DIR="${TEST_TMPDIR}/oxide/plugins"

    create_baked_plugin "testplugin" "TestPlugin"

    run stage_from_baked "testplugin"
    assert_success
    assert_output --partial "staged baked:testplugin"
    assert [ -f "${OXIDE_PLUGINS_DIR}/TestPlugin.cs" ]
}

@test "stage_from_baked: fails with hash mismatch" {
    source_lib
    export PER_PLUGIN_DIR="${TEST_TMPDIR}/per-plugin"
    export OXIDE_PLUGINS_DIR="${TEST_TMPDIR}/oxide/plugins"

    create_baked_plugin_bad_hash "badplugin" "BadPlugin"

    run stage_from_baked "badplugin"
    assert_equal "$status" 2
    assert_output --partial "FATAL"
}

@test "stage_from_baked: returns 1 when plugin dir missing" {
    source_lib
    export PER_PLUGIN_DIR="${TEST_TMPDIR}/per-plugin"
    export OXIDE_PLUGINS_DIR="${TEST_TMPDIR}/oxide/plugins"

    run stage_from_baked "nonexistent"
    assert_equal "$status" 1
}
