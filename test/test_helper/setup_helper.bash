#!/usr/bin/env bash
# Common test setup for penguin-rust-base bats tests.
# Provides mock infrastructure and shared fixtures.

# Project root
export PROJECT_ROOT="${BATS_TEST_DIRNAME}/.."
export DOCKER_DIR="${PROJECT_ROOT}/docker"

# Create a temporary sandbox for each test
setup() {
    export TEST_TMPDIR="$(mktemp -d)"
    export OXIDE_PLUGINS_DIR="${TEST_TMPDIR}/oxide/plugins"
    export PER_PLUGIN_DIR="${TEST_TMPDIR}/per-plugin"
    mkdir -p "${OXIDE_PLUGINS_DIR}" "${PER_PLUGIN_DIR}"

    # Default env vars (tests can override)
    export PLUGIN_SOURCE="github"
    export RUST_PLUGINS=""
    export WIPE_SCHED=""
    export WIPE_DAY="Th"
    export WIPE_TIME="06:00"
    export WIPE_BP="false"
    export OXIDE="1"
    export RUST_SERVER_IDENTITY="test_server"
    export RUST_RCON_PASSWORD="testpass"
    export RUST_RCON_PORT="28016"
    export PLUGIN_UPDATE_ENABLED="1"

    # Mock websocat — record calls instead of sending RCON
    export RCON_LOG="${TEST_TMPDIR}/rcon.log"
    mkdir -p "${TEST_TMPDIR}/mock-bin"
    cat > "${TEST_TMPDIR}/mock-bin/websocat" <<'MOCK'
#!/bin/bash
# Record the RCON message for test assertions
cat >> "${RCON_LOG}" 2>/dev/null
exit 0
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/websocat"

    # Mock curl — configurable per test via MOCK_CURL_HANDLER
    cat > "${TEST_TMPDIR}/mock-bin/curl" <<'MOCK'
#!/bin/bash
if [ -n "${MOCK_CURL_HANDLER}" ] && [ -f "${MOCK_CURL_HANDLER}" ]; then
    source "${MOCK_CURL_HANDLER}" "$@"
else
    # Default: succeed with empty output
    exit 0
fi
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/curl"

    # Mock pgrep — default: server is running
    cat > "${TEST_TMPDIR}/mock-bin/pgrep" <<'MOCK'
#!/bin/bash
exit 0
MOCK
    chmod +x "${TEST_TMPDIR}/mock-bin/pgrep"

    # Prepend mock bin to PATH
    export ORIGINAL_PATH="${PATH}"
    export PATH="${TEST_TMPDIR}/mock-bin:${PATH}"
}

teardown() {
    export PATH="${ORIGINAL_PATH}"
    rm -rf "${TEST_TMPDIR}"
}

# Helper: create a fake baked plugin with known hash
create_baked_plugin() {
    local slug="$1"
    local filename="${2:-${slug^}}"  # Default: capitalize slug
    local content="${3:-// Plugin ${slug}}"

    local plugin_dir="${PER_PLUGIN_DIR}/${slug}"
    mkdir -p "${plugin_dir}"
    printf '%s\n' "${content}" > "${plugin_dir}/${filename}.cs"
    sha256sum "${plugin_dir}/${filename}.cs" | sed "s|${plugin_dir}/||" > "${plugin_dir}/${slug}.hash"
}

# Helper: create a baked plugin with a BAD hash (mismatch)
create_baked_plugin_bad_hash() {
    local slug="$1"
    local filename="${2:-${slug^}}"

    local plugin_dir="${PER_PLUGIN_DIR}/${slug}"
    mkdir -p "${plugin_dir}"
    printf '// Plugin %s\n' "${slug}" > "${plugin_dir}/${filename}.cs"
    echo "0000000000000000000000000000000000000000000000000000000000000000  ${filename}.cs" \
        > "${plugin_dir}/${slug}.hash"
}
