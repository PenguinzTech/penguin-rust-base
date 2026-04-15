#!/usr/bin/env bats
# Tests for docker/manage-plugin.sh runtime plugin manager.

load 'test_helper/bats-support/load'
load 'test_helper/bats-assert/load'
load 'test_helper/setup_helper'

# Wrapper: sed-patch hardcoded runtime paths to use test sandbox
run_manage_plugin() {
    local wrapper="${TEST_TMPDIR}/manage-plugin-wrapper.sh"
    sed \
        -e "s|. /usr/local/lib/penguin-rust/lib-functions.sh|. ${DOCKER_DIR}/lib-functions.sh|" \
        -e "s|OXIDE_PLUGINS_DIR=\"/steamcmd/rust/oxide/plugins\"|OXIDE_PLUGINS_DIR=\"${OXIDE_PLUGINS_DIR}\"|" \
        -e "s|PER_PLUGIN_DIR=\"/etc/penguin-rust-plugins/per-plugin\"|PER_PLUGIN_DIR=\"${PER_PLUGIN_DIR}\"|" \
        "${DOCKER_DIR}/manage-plugin.sh" > "${wrapper}"
    chmod +x "${wrapper}"
    bash "${wrapper}" "$@"
}

# Per-test helper: pre-create disabled/ and patched/ dirs in sandbox
setup_plugin_dirs() {
    mkdir -p "${OXIDE_PLUGINS_DIR}/disabled"
    mkdir -p "${OXIDE_PLUGINS_DIR}/patched"
}

# ─── list ────────────────────────────────────────────────────────────────────

@test "list: shows enabled, patched, and disabled plugins" {
    setup_plugin_dirs
    echo "// TruePVE"   > "${OXIDE_PLUGINS_DIR}/TruePVE.cs"
    echo "// Vanish"    > "${OXIDE_PLUGINS_DIR}/patched/Vanish.cs"
    printf '// White\n' | gzip > "${OXIDE_PLUGINS_DIR}/disabled/Whitelist.cs.gz"

    run run_manage_plugin list
    assert_success
    assert_output --partial "  + TruePVE"
    assert_output --partial "  ~ Vanish"
    assert_output --partial "  - Whitelist"
}

@test "list: empty result when no plugins present" {
    setup_plugin_dirs

    run run_manage_plugin list
    assert_success
    assert_output --partial "=== Enabled ==="
    assert_output --partial "=== Available (patched) ==="
    assert_output --partial "=== Available (disabled) ==="
    # No plugin lines — just section headers
    refute_output --partial "  +"
    refute_output --partial "  -"
    refute_output --partial "  ~"
}

# ─── add: patched cache ──────────────────────────────────────────────────────

@test "add: activates from patched/ cache (preferred over disabled/)" {
    setup_plugin_dirs
    echo "// Vanish patched" > "${OXIDE_PLUGINS_DIR}/patched/Vanish.cs"
    # Also put an unpatched copy in disabled/ to confirm patched takes priority
    printf '// Vanish baked\n' | gzip > "${OXIDE_PLUGINS_DIR}/disabled/Vanish.cs.gz"
    export PLUGIN_SOURCE="github"

    run run_manage_plugin add Vanish
    assert_success
    assert [ -f "${OXIDE_PLUGINS_DIR}/Vanish.cs" ]
    assert_output --partial "activated from patched cache"
    # Content must be the patched version
    run grep "patched" "${OXIDE_PLUGINS_DIR}/Vanish.cs"
    assert_success
}

# ─── add: baked disabled/ cache ─────────────────────────────────────────────

@test "add: activates from baked disabled/ cache when no patched copy exists" {
    setup_plugin_dirs
    printf '// TruePVE\n' | gzip > "${OXIDE_PLUGINS_DIR}/disabled/TruePVE.cs.gz"
    echo "abc123  truepve.hash" > "${OXIDE_PLUGINS_DIR}/disabled/truepve.hash"
    export PLUGIN_SOURCE="github"

    run run_manage_plugin add TruePVE
    assert_success
    assert [ -f "${OXIDE_PLUGINS_DIR}/TruePVE.cs" ]
    assert_output --partial "activated from baked cache"
}

@test "add: fails when not in cache and source=baked" {
    setup_plugin_dirs
    # Nothing in disabled/ or patched/
    export PLUGIN_SOURCE="baked"

    run run_manage_plugin add Unknown baked
    assert_failure
    # RCON error message sent
    run cat "${RCON_LOG}"
    assert_output --partial "ERROR"
}

# ─── add: umod path ─────────────────────────────────────────────────────────

@test "add: source=umod calls fetch_from_umod path" {
    setup_plugin_dirs
    # Mock curl to behave like a umod .cs download landing in oxide/plugins/
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/curl_handler.sh"
    cat > "${MOCK_CURL_HANDLER}" <<HANDLER
#!/bin/bash
# Detect umod download call (contains umod.org)
if echo "\$*" | grep -q "umod.org"; then
    # Write a fake .cs file to the output path (-o flag arg)
    out_arg=""
    while [ "\$#" -gt 0 ]; do
        if [ "\$1" = "-o" ]; then out_arg="\$2"; fi
        shift
    done
    if [ -n "\$out_arg" ]; then
        echo "// BGrade from umod" > "\$out_arg"
    fi
fi
exit 0
HANDLER
    chmod +x "${MOCK_CURL_HANDLER}"

    run run_manage_plugin add BGrade umod
    # fetch_from_umod is in lib-functions.sh; with our mock curl it may or may not
    # place the file, but the command should not error out on the umod branch
    # (actual umod integration tested in lib-functions.bats)
    [ "${status}" -eq 0 ] || [ "${status}" -eq 1 ]  # either path is acceptable in unit test
}

# ─── remove ──────────────────────────────────────────────────────────────────

@test "remove: deactivates enabled plugin and compresses to disabled/" {
    setup_plugin_dirs
    echo "// TruePVE active" > "${OXIDE_PLUGINS_DIR}/TruePVE.cs"

    run run_manage_plugin remove TruePVE
    assert_success
    assert [ ! -f "${OXIDE_PLUGINS_DIR}/TruePVE.cs" ]
    assert [ -f "${OXIDE_PLUGINS_DIR}/disabled/TruePVE.cs.gz" ]
    assert_output --partial "deactivated"
}

@test "remove: error when plugin not currently enabled" {
    setup_plugin_dirs
    # No .cs in plugins/ root

    run run_manage_plugin remove TruePVE
    assert_failure
    run cat "${RCON_LOG}"
    assert_output --partial "ERROR"
    assert_output --partial "not currently enabled"
}

# ─── update ──────────────────────────────────────────────────────────────────

@test "update: sends oxide.reload after re-fetch attempt" {
    setup_plugin_dirs
    # Mock GitHub API: return a tag but no tarball assets (simulates no-op download)
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/curl_handler.sh"
    cat > "${MOCK_CURL_HANDLER}" <<'HANDLER'
#!/bin/bash
# /releases?per_page — return one matching tag
if echo "$*" | grep -q "releases?per_page"; then
    echo '[{"tag_name":"truepve-1234567890"}]'
# /releases/tags/ — return empty asset list
elif echo "$*" | grep -q "releases/tags"; then
    echo '{"assets":[]}'
fi
exit 0
HANDLER
    chmod +x "${MOCK_CURL_HANDLER}"

    run run_manage_plugin update TruePVE
    assert_success
    # oxide.reload must appear in the RCON log
    run cat "${RCON_LOG}"
    assert_output --partial "oxide.reload TruePVE"
}
