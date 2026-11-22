#!/usr/bin/env bash
# Run the release acceptance from a clean extraction, with only the selected engine.
set -euo pipefail
umask 077

mode=${1:?Usage: verify-assets.sh compose|quadlet TAG ASSETS [EXPECTED_REVISION]}
tag=${2:?Specify the deployment release tag}
assets=$(realpath -- "${3:?Specify the assets directory}")
revision=${4:-}
case "$mode" in
    compose) engine=docker ;;
    quadlet) engine=podman ;;
    *) exit 2 ;;
esac
[[ "$tag" =~ ^deploy-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]
name="monstertacle-hosting-$tag-linux-amd64"
bundle="$name-bundle.tar.gz"
images="$name-images.tar.gz"
scratch=$(mktemp -d /tmp/hosting-verify-release.XXXXXXXX)
trap 'rm -rf -- "$scratch"' EXIT
mapfile -t sums < "$assets/SHA256SUMS"
[[ ${#sums[@]} == 2 && "${sums[0]:66}" == "$bundle" && "${sums[1]:66}" == "$images" ]]
(cd -- "$assets" && sha256sum --strict --check SHA256SUMS)
tar -xzf "$assets/$bundle" -C "$scratch"
cd -- "$scratch/$name"
[[ "$(cat version)" == "$tag" ]]
if [[ -n "$revision" ]]; then [[ "$(cat revision)" == "$revision" ]]; fi
if [[ -n "$(find . \( -name .git -o -name '*.cs' -o -name '*.csproj' -o -name Dockerfile \) -print -quit)" ]]; then
    printf 'Unexpected checkout or application source in the bundle.\n' >&2
    exit 1
fi
"$engine" load --input "$assets/$images"
bash ./quickstart.sh "$mode"
bash ./quickstart.sh "$mode" enrollment
if [[ "$mode" == compose ]]; then bash ./quickstart.sh compose keycloak; fi
