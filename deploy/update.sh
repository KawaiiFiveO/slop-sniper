#!/usr/bin/env bash
# Updates slop-sniper in this directory to the latest GitHub release and restarts the service.
# config.json and state.json are never touched. If the new version doesn't stay running, the previous
# one is restored automatically. Also does the first download into an empty directory.
#
# Usage:
#   ./update.sh                    Install the latest release
#   ./update.sh --version v1.0.1   Install a specific release (also works for pre-releases)
#   ./update.sh --force            Reinstall even if already up to date
#   ./update.sh --rollback         Swap back to the previously installed version
#
# While the repo is private, put a GitHub token with read access to it in .github-token next to this
# script (or in GITHUB_TOKEN). Run as the bot's user, not root; sudo is only used to restart the service.
set -euo pipefail

REPO="${SLOPSNIPER_REPO:-KawaiiFiveO/slop-sniper}"
SERVICE_NAME="${SERVICE_NAME:-slop-sniper}"
ASSET="slop-sniper-linux-x64.tar.gz"
HEALTH_CHECK_SECONDS=15
INSTALL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Files shipped in a release. config.json, state.json and .github-token are deliberately not in this list.
RELEASE_FILES=(SlopSniper VERSION install.sh update.sh slop-sniper.service.template config.example.json)

die() { echo "Error: $*" >&2; exit 1; }

installed_version() { cat "${INSTALL_DIR}/VERSION" 2>/dev/null || echo "none"; }

github_token() {
    if [[ -n "${GITHUB_TOKEN:-}" ]]; then
        echo "${GITHUB_TOKEN}"
    elif [[ -f "${INSTALL_DIR}/.github-token" ]]; then
        tr -d '[:space:]' <"${INSTALL_DIR}/.github-token"
    fi
}

# Fetches a GitHub API URL, authenticated when a token is available.
api_get() {
    local token
    token="$(github_token)"
    if [[ -n "${token}" ]]; then
        curl -fsSL -H "Authorization: Bearer ${token}" -H "Accept: application/vnd.github+json" "$1"
    else
        curl -fsSL -H "Accept: application/vnd.github+json" "$1"
    fi
}

# Prints the API URL of a release asset, from the release JSON in $1 and the asset name in $2. The JSON is
# split into one "key":value per line; each asset's "url" comes before its "name".
asset_api_url() {
    tr ',{}[]' '\n\n\n\n\n' <<<"$1" \
        | sed 's/^[[:space:]]*//; s/[[:space:]]*$//; s/": */":/' \
        | awk -v name="\"name\":\"$2\"" '
            /^"url":"https:\/\/api\.github\.com\/repos\/.*\/releases\/assets\/[0-9]+"$/ { url = substr($0, 8, length($0) - 8) }
            $0 == name && url != "" { print url; exit }
        '
}

# Downloads release asset $2 to $3. $1 is the release JSON.
download_asset() {
    local url token
    url="$(asset_api_url "$1" "$2")"
    [[ -n "${url}" ]] || return 1
    token="$(github_token)"
    # curl drops the Authorization header when GitHub redirects to its file storage, which is what that host expects.
    if [[ -n "${token}" ]]; then
        curl -fL --retry 3 --progress-bar -H "Authorization: Bearer ${token}" -H "Accept: application/octet-stream" -o "$3" "${url}"
    else
        curl -fL --retry 3 --progress-bar -H "Accept: application/octet-stream" -o "$3" "${url}"
    fi
}

# Restarts the service and checks it is still running after a short wait, which catches a rejected
# token or a config error that only shows up once the bot connects.
restart_and_check() {
    echo "Restarting ${SERVICE_NAME}..."
    sudo systemctl restart "${SERVICE_NAME}" || return 1
    echo "Waiting ${HEALTH_CHECK_SECONDS}s to make sure it stays up..."
    sleep "${HEALTH_CHECK_SECONDS}"
    systemctl is-active --quiet "${SERVICE_NAME}"
}

show_logs() {
    echo "Recent logs:" >&2
    sudo journalctl -u "${SERVICE_NAME}" -n 30 --no-pager >&2 || true
}

# Swaps SlopSniper/VERSION with SlopSniper.old/VERSION.old, so running it twice switches back again.
swap_with_previous() {
    [[ -f "${INSTALL_DIR}/SlopSniper.old" && -f "${INSTALL_DIR}/VERSION.old" ]] || die "no previous version to roll back to"
    cd "${INSTALL_DIR}"
    mv -f SlopSniper SlopSniper.swap
    mv -f SlopSniper.old SlopSniper
    mv -f SlopSniper.swap SlopSniper.old
    mv -f VERSION VERSION.swap
    mv -f VERSION.old VERSION
    mv -f VERSION.swap VERSION.old
}

rollback() {
    swap_with_previous
    echo "Rolled back to version $(installed_version)."
    restart_and_check || { show_logs; die "the previous version didn't stay running either"; }
    echo "slop-sniper is running."
}

[[ $EUID -ne 0 ]] || die "run as the bot's user, not root (sudo is only used to restart the service)"

target_tag=""
force=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --rollback) rollback; exit 0 ;;
        --version) [[ $# -ge 2 ]] || die "--version needs a tag, e.g. --version v1.0.1"; target_tag="$2"; shift 2 ;;
        --force) force=true; shift ;;
        -h|--help) sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "unknown option: $1 (see --help)" ;;
    esac
done

if [[ -f "${INSTALL_DIR}/.github-token" ]]; then
    chmod 600 "${INSTALL_DIR}/.github-token"
fi

if [[ -z "${target_tag}" ]]; then
    release_json="$(api_get "https://api.github.com/repos/${REPO}/releases/latest")" \
        || die "couldn't fetch the latest release from GitHub (private repo: is .github-token set?)"
else
    release_json="$(api_get "https://api.github.com/repos/${REPO}/releases/tags/${target_tag}")" \
        || die "couldn't find release ${target_tag} on GitHub"
fi
target_tag="$(sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' <<<"${release_json}" | head -n 1)"
[[ -n "${target_tag}" ]] || die "couldn't find the release tag"

current="$(installed_version)"
if [[ "${current}" == "${target_tag#v}" && "${force}" == false ]]; then
    echo "Already up to date (${current})."
    exit 0
fi
echo "Updating slop-sniper: ${current} -> ${target_tag#v}"

work_dir="$(mktemp -d)"
trap 'rm -rf "${work_dir}"' EXIT

download_asset "${release_json}" "${ASSET}" "${work_dir}/${ASSET}" \
    || die "couldn't download ${ASSET} for ${target_tag}"
download_asset "${release_json}" "${ASSET}.sha256" "${work_dir}/${ASSET}.sha256" \
    || die "couldn't download the checksum for ${target_tag}"
(cd "${work_dir}" && sha256sum --check --quiet "${ASSET}.sha256") || die "checksum mismatch, not installing"

mkdir "${work_dir}/release"
tar -xzf "${work_dir}/${ASSET}" -C "${work_dir}/release"
[[ -f "${work_dir}/release/SlopSniper" ]] || die "the release archive doesn't contain SlopSniper"

# First download into this directory: there's no service to restart yet.
if [[ ! -f "${INSTALL_DIR}/SlopSniper" ]]; then
    for file in "${RELEASE_FILES[@]}"; do
        if [[ -f "${work_dir}/release/${file}" ]]; then
            mv -f "${work_dir}/release/${file}" "${INSTALL_DIR}/${file}"
        fi
    done
    chmod +x "${INSTALL_DIR}/SlopSniper" "${INSTALL_DIR}/install.sh" "${INSTALL_DIR}/update.sh"
    echo "Downloaded slop-sniper ${target_tag#v}. Next: cp config.example.json config.json, fill it in, then sudo ./install.sh"
    exit 0
fi

if [[ -f "${INSTALL_DIR}/slop-sniper.service.template" ]] \
    && ! cmp -s "${INSTALL_DIR}/slop-sniper.service.template" "${work_dir}/release/slop-sniper.service.template"; then
    unit_changed=true
else
    unit_changed=false
fi

# Set the running version aside. It only becomes the --rollback target once the new version proves
# healthy, so a failed update never leaves the broken release as "previous".
# mv replaces files without disturbing the running process.
cd "${INSTALL_DIR}"
rm -f SlopSniper.pending-old VERSION.pending-old
cp -p SlopSniper SlopSniper.pending-old
cp -p VERSION VERSION.pending-old
for file in "${RELEASE_FILES[@]}"; do
    if [[ -f "${work_dir}/release/${file}" ]]; then
        mv -f "${work_dir}/release/${file}" "${INSTALL_DIR}/${file}"
    fi
done
chmod +x SlopSniper install.sh update.sh

if restart_and_check; then
    mv -f SlopSniper.pending-old SlopSniper.old
    mv -f VERSION.pending-old VERSION.old
    echo "slop-sniper ${target_tag#v} is running. Undo with: ./update.sh --rollback"
    if [[ "${unit_changed}" == true ]]; then
        echo "Note: the service definition changed in this release. Apply it with: sudo ./install.sh"
    fi
else
    echo "The new version didn't stay running." >&2
    show_logs
    echo "Restoring version ${current}..." >&2
    mv -f SlopSniper.pending-old SlopSniper
    mv -f VERSION.pending-old VERSION
    restart_and_check || { show_logs; die "version ${current} didn't stay running either"; }
    echo "Version ${current} is running again." >&2
    exit 1
fi
