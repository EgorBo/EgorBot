#!/usr/bin/env bash

set -euo pipefail

SAMPLY_VERSION="0.13.1"
SAMPLY_RELEASE_TAG="samply-v${SAMPLY_VERSION}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROFILE_OUT="${PROFILE_OUT:-}"
TOP="${TOP:-20}"
PYTHON_BIN="${PYTHON_BIN:-python3}"
SAMPLY_RATE="${SAMPLY_RATE:-1000}"
SAMPLY_TOOLS_DIR="${SAMPLY_TOOLS_DIR:-${SCRIPT_DIR}/.tools}"

log()
{
    printf '[SAMPLY] %s\n' "$*"
}

fail()
{
    log "ERROR: $*"
    exit 1
}

if [[ -z "${PROFILE_OUT}" ]]; then
    fail "PROFILE_OUT must point to the per-run output directory"
fi

if [[ $# -eq 0 ]]; then
    fail "No command was supplied to profile"
fi

mkdir -p "${PROFILE_OUT}"
STATUS_FILE="${PROFILE_OUT}/run-status.txt"
: > "${STATUS_FILE}"

status()
{
    printf '%s\n' "$*" >> "${STATUS_FILE}"
}

ensure_samply()
{
    local machine
    local target
    local expected_sha
    local archive_name
    local archive
    local install_dir
    local temp_dir
    local actual_sha

    machine="$(uname -m)"
    case "${machine}" in
        arm64|aarch64)
            target="aarch64-apple-darwin"
            expected_sha="7597239aa3769e75058be5ed359dbfe067f5e7714a5f052c45dd81d509aec17f"
            ;;
        x86_64)
            target="x86_64-apple-darwin"
            expected_sha="a57f05f9162d06c36df6d51425d976ac774a8cd6dbd84050e6303fa8bf813998"
            ;;
        *)
            fail "Unsupported macOS architecture reported by uname: ${machine}"
            ;;
    esac

    archive_name="samply-${target}.tar.xz"
    archive="${SAMPLY_TOOLS_DIR}/${archive_name}"
    install_dir="${SAMPLY_TOOLS_DIR}/samply-${SAMPLY_VERSION}-${target}"
    SAMPLY_BIN="${install_dir}/samply"

    if [[ ! -x "${SAMPLY_BIN}" ]]; then
        mkdir -p "${SAMPLY_TOOLS_DIR}"
        log "Downloading Samply ${SAMPLY_VERSION} for ${target}..."
        curl --fail --location --retry 3 --retry-delay 2 \
            --output "${archive}" \
            "https://github.com/mstange/samply/releases/download/${SAMPLY_RELEASE_TAG}/${archive_name}" \
            || fail "Could not download ${archive_name}"

        actual_sha="$(shasum -a 256 "${archive}" | awk '{print $1}')"
        if [[ "${actual_sha}" != "${expected_sha}" ]]; then
            rm -f "${archive}"
            fail "Checksum mismatch for ${archive_name}: expected ${expected_sha}, got ${actual_sha}"
        fi
        log "Verified ${archive_name} SHA-256: ${actual_sha}"

        temp_dir="${install_dir}.extracting"
        rm -rf "${temp_dir}"
        mkdir -p "${temp_dir}"
        tar -xJf "${archive}" -C "${temp_dir}" \
            || fail "Could not extract ${archive_name}"
        rm -rf "${install_dir}"
        mv "${temp_dir}/samply-${target}" "${install_dir}" \
            || fail "Unexpected directory layout in ${archive_name}"
        rm -rf "${temp_dir}"
        chmod +x "${SAMPLY_BIN}"
    else
        log "Using cached Samply binary: ${SAMPLY_BIN}"
    fi

    local version_output
    version_output="$("${SAMPLY_BIN}" --version 2>&1)" \
        || fail "Cached Samply binary cannot run: ${SAMPLY_BIN}"
    log "${version_output}"
    if [[ "${version_output}" != *"${SAMPLY_VERSION}"* ]]; then
        fail "Expected Samply ${SAMPLY_VERSION}, got: ${version_output}"
    fi

    log "Applying the local macOS debugger entitlement to Samply..."
    "${SAMPLY_BIN}" setup --yes \
        || fail "Samply code-signing setup failed"

    local entitlements
    entitlements="$(codesign -d --entitlements :- "${SAMPLY_BIN}" 2>&1 || true)"
    if [[ "${entitlements}" != *"com.apple.security.cs.debugger"* ]]; then
        log "codesign output:"
        printf '%s\n' "${entitlements}"
        fail "Samply does not have the com.apple.security.cs.debugger entitlement"
    fi
    log "Verified Samply debugger entitlement"

    status "samply_version=${version_output}"
    status "samply_binary=${SAMPLY_BIN}"
    status "machine=$(uname -a)"
}

ensure_samply

log "Profile output: ${PROFILE_OUT}"
log "Sampling rate: ${SAMPLY_RATE} Hz"
log "Annotated assembly limit: ${TOP} functions"
printf '[SAMPLY] Target command:'
printf ' %q' "$@"
printf '\n'

status "profile_out=${PROFILE_OUT}"
status "sampling_rate_hz=${SAMPLY_RATE}"
status "target_command=$(printf '%q ' "$@")"

profile_name_args=()
if [[ -n "${SAMPLY_PROFILE_NAME:-}" ]]; then
    profile_name_args=(--profile-name "${SAMPLY_PROFILE_NAME}")
fi

set +e
DOTNET_PerfMapEnabled=2 \
DOTNET_PerfMapJitDumpPath="${PROFILE_OUT}" \
DOTNET_PerfMapStubGranularity=4 \
DOTNET_PerfMapShowOptimizationTiers=1 \
"${SAMPLY_BIN}" record \
    --save-only \
    --unstable-presymbolicate \
    --rate "${SAMPLY_RATE}" \
    --output "${PROFILE_OUT}/profile.json.gz" \
    "${profile_name_args[@]}" \
    -- "$@"
target_exit=$?
set -e

status "target_exit=${target_exit}"
log "Samply record finished with exit code ${target_exit}"

profile_file="${PROFILE_OUT}/profile.json.gz"
symbols_file="${PROFILE_OUT}/profile.json.syms.json"
report_exit=0

if [[ ! -s "${profile_file}" ]]; then
    log "ERROR: Samply did not create ${profile_file}"
    report_exit=1
elif [[ ! -s "${symbols_file}" ]]; then
    log "ERROR: Samply did not create ${symbols_file}"
    report_exit=1
else
    log "Raw profile: $(du -h "${profile_file}" | awk '{print $1}')"
    log "Symbol sidecar: $(du -h "${symbols_file}" | awk '{print $1}')"

    set +e
    "${PYTHON_BIN}" "${SCRIPT_DIR}/samply-report.py" \
        --samply "${SAMPLY_BIN}" \
        --profile "${profile_file}" \
        --speedscope "${PROFILE_OUT}/flamegraph.speedscope.json" \
        --assembly "${PROFILE_OUT}/annotated-asm.txt" \
        --top "${TOP}"
    report_exit=$?
    set -e
fi

status "report_exit=${report_exit}"
log "Report generation finished with exit code ${report_exit}"
log "Files left in the profile directory:"
find "${PROFILE_OUT}" -maxdepth 1 -type f -exec ls -lh {} \; 2>/dev/null || true

if [[ ${report_exit} -ne 0 ]]; then
    exit "${report_exit}"
fi

exit "${target_exit}"
