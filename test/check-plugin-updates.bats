#!/usr/bin/env bats
# Tests for docker/check-plugin-updates.sh plugin update checker.

load 'test_helper/bats-support/load'
load 'test_helper/bats-assert/load'
load 'test_helper/setup_helper'

# Create a wrapper that sources lib-functions.sh from our docker dir
# and overrides hardcoded paths to use test sandbox
run_check_updates() {
    local wrapper="${TEST_TMPDIR}/check-updates-wrapper.sh"
    sed -e "s|. /usr/local/lib/penguin-rust/lib-functions.sh|. ${DOCKER_DIR}/lib-functions.sh|" \
        -e "s|PER_PLUGIN_DIR=\"/etc/penguin-rust-plugins/per-plugin\"|PER_PLUGIN_DIR=\"${PER_PLUGIN_DIR}\"|" \
        -e "s|OXIDE_PLUGINS_DIR=\"/steamcmd/rust/oxide/plugins\"|OXIDE_PLUGINS_DIR=\"${OXIDE_PLUGINS_DIR}\"|" \
        "${DOCKER_DIR}/check-plugin-updates.sh" > "${wrapper}"
    chmod +x "${wrapper}"
    bash "${wrapper}"
}

# ─── Guards ─────────────────────────────────────────────────────────────────

@test "check-updates: exits when PLUGIN_UPDATE_ENABLED=0" {
    export PLUGIN_UPDATE_ENABLED="0"
    run run_check_updates
    assert_success
    refute_output
}

@test "check-updates: exits when PLUGIN_SOURCE=umod" {
    export PLUGIN_SOURCE="umod"
    run run_check_updates
    assert_success
    refute_output
}

@test "check-updates: exits when server not running" {
    cat > "${TEST_TMPDIR}/mock-bin/pgrep" <<'MOCK'
#!/bin/bash
exit 1
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/pgrep"

    run run_check_updates
    assert_success
    refute_output
}

# ─── No tags available (API rate limit / network issue) ─────────────────────

@test "check-updates: exits silently when API returns no tags" {
    # Default mock curl returns empty output
    run run_check_updates
    assert_success
    refute_output
}

# ─── No update needed (hash matches) ───────────────────────────────────────

@test "check-updates: skips plugin when hash matches" {
    create_baked_plugin "testplugin" "TestPlugin" "// Plugin v1"
    local current_sha
    current_sha=$(awk '{print $1}' "${PER_PLUGIN_DIR}/testplugin/testplugin.hash")

    # Mock curl to return tags and matching hash
    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<HANDLER
for arg in "\$@"; do
    case "\${arg}" in
        *releases?per_page=100)
            echo '[{"tag_name": "testplugin-1710000000"}]'
            exit 0
            ;;
        *testplugin.hash)
            echo "${current_sha}  TestPlugin.cs"
            exit 0
            ;;
    esac
done
exit 0
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run run_check_updates
    assert_success
    refute_output --partial "update available"
}

# ─── Update available (hash mismatch) ──────────────────────────────────────

@test "check-updates: detects update when hash differs" {
    create_baked_plugin "testplugin" "TestPlugin" "// Plugin v1"

    # Mock curl: return different upstream hash, then fail on tarball download
    # (we just want to test detection, not the full download flow)
    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<'HANDLER'
for arg in "$@"; do
    case "${arg}" in
        *releases?per_page=100)
            echo '[{"tag_name": "testplugin-1710000001"}]'
            exit 0
            ;;
        *testplugin.hash)
            echo "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  TestPlugin.cs"
            exit 0
            ;;
        *releases/tags/*)
            echo '{"assets": []}'
            exit 0
            ;;
    esac
done
exit 0
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run run_check_updates
    assert_success
    assert_output --partial "update available"
    assert_output --partial "WARNING: no tarball asset found"
}

# ─── Tag matching ───────────────────────────────────────────────────────────

@test "check-updates: picks latest tag by epoch suffix" {
    create_baked_plugin "myplugin" "MyPlugin" "// Plugin old"

    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<'HANDLER'
for arg in "$@"; do
    case "${arg}" in
        *releases?per_page=100)
            # Multiple tags — should pick the one with highest epoch
            cat <<'TAGS'
[
    {"tag_name": "myplugin-1700000000"},
    {"tag_name": "myplugin-1710000000"},
    {"tag_name": "myplugin-1705000000"},
    {"tag_name": "otherplugin-9999999999"}
]
TAGS
            exit 0
            ;;
        *download/myplugin-1710000000/myplugin.hash)
            echo "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  MyPlugin.cs"
            exit 0
            ;;
        *releases/tags/myplugin-1710000000)
            echo '{"assets": []}'
            exit 0
            ;;
    esac
done
exit 0
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run run_check_updates
    assert_success
    assert_output --partial "update available"
}

# ─── Multiple plugins ──────────────────────────────────────────────────────

@test "check-updates: checks all baked plugins" {
    create_baked_plugin "plugina" "PluginA" "// A v1"
    create_baked_plugin "pluginb" "PluginB" "// B v1"

    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<'HANDLER'
for arg in "$@"; do
    case "${arg}" in
        *releases?per_page=100)
            echo '[{"tag_name": "plugina-1710000000"}, {"tag_name": "pluginb-1710000000"}]'
            exit 0
            ;;
        *plugina.hash)
            echo "aaaa0000000000000000000000000000000000000000000000000000aaaa0000  PluginA.cs"
            exit 0
            ;;
        *pluginb.hash)
            echo "bbbb0000000000000000000000000000000000000000000000000000bbbb0000  PluginB.cs"
            exit 0
            ;;
        *releases/tags/*)
            echo '{"assets": []}'
            exit 0
            ;;
    esac
done
exit 0
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run run_check_updates
    assert_success
    assert_output --partial "plugina: update available"
    assert_output --partial "pluginb: update available"
}

# ─── Plugin with no hash file ──────────────────────────────────────────────

@test "check-updates: skips plugin directory without hash file" {
    # Create a plugin dir without a hash file
    mkdir -p "${PER_PLUGIN_DIR}/nohash"
    echo "// no hash" > "${PER_PLUGIN_DIR}/nohash/NoHash.cs"

    # Tags exist for this slug
    cat > "${TEST_TMPDIR}/mock-curl-handler.sh" <<'HANDLER'
for arg in "$@"; do
    case "${arg}" in
        *releases?per_page=100)
            echo '[{"tag_name": "nohash-1710000000"}]'
            exit 0
            ;;
    esac
done
exit 0
HANDLER
    export MOCK_CURL_HANDLER="${TEST_TMPDIR}/mock-curl-handler.sh"

    run run_check_updates
    assert_success
    refute_output --partial "nohash"
}
