#!/usr/bin/env bash
set -euo pipefail
umask 077

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
engine=${1:?Usage: build-assets.sh docker|podman deploy-vX.Y.Z OUTPUT}
tag=${2:?Specify the deployment release tag}
output=${3:?Specify a new output directory}
[[ "$engine" == docker || "$engine" == podman ]]
[[ "$tag" =~ ^deploy-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]
[[ "${#tag}" -le 64 ]]
[[ "$(uname -sm)" == 'Linux x86_64' ]]
mkdir -- "$output"
output=$(realpath -- "$output")
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
name="monstertacle-hosting-$tag-linux-amd64"
bundle="$work/$name"
mkdir -p "$bundle/quadlet" "$bundle/fixture/quadlet"

# Preserve the checked-in deployment settings, excluding checkout-only builds.
sed '/^    build:$/,+2d' "$repo/deploy/compose.yaml" |
    sed -E "/^    image:/ {s/:local$/:$tag/; a\    pull_policy: never
    }" > "$bundle/compose.yaml"
for unit in "$repo"/deploy/quadlet/*.{container,volume,network}; do
    sed -E "/^Image=/ {s/:local$/:$tag/; a\Pull=never
    }" "$unit" > "$bundle/quadlet/${unit##*/}"
done

awk '/^  worker:$/ {worker=1; print; next} /^  edge:$/ {worker=0} worker' "$bundle/compose.yaml" |
    sed -e 's/^  worker:/  worker-secondary:/' -e 's@/worker\([./:]\)@/worker-secondary\1@g' \
        -e 's/worker-state:/worker-secondary-state:/' > "$work/worker-secondary.yaml"
awk -v secondary="$work/worker-secondary.yaml" '
    /^  edge:$/ {while ((getline line < secondary) > 0) print line; close(secondary)}
    {print}
    /^  worker-state:$/ { print "  worker-secondary-state:" }
' "$bundle/compose.yaml" > "$work/compose.yaml"
mv "$work/compose.yaml" "$bundle/compose.yaml"
sed -e 's@/worker\([./:]\)@/worker-secondary\1@g' \
    -e 's/hosting-worker-state/hosting-worker-secondary-state/' \
    "$bundle/quadlet/hosting-worker.container" > "$bundle/quadlet/hosting-worker-secondary.container"
cp "$bundle/quadlet/"hosting-worker{,-secondary}-state.volume

cp "$repo/deploy/release/"{README.md,quickstart.sh,fixture-config.sh,fixture-enrollment.sh,fixture-keycloak.sh} "$bundle/"
mkdir -p "$bundle/keycloak" "$bundle/enrollment/quadlet/"{hosting-broker.container.d,hosting-edge.container.d}
reference_files=(
    enrollment/{README.md,compose.yaml,enroll.sh,policy.example.json}
    keycloak/{README.md,compose.yaml,bootstrap.compose.yaml,clients.example.json,start-reference.sh,nginx.conf,render-realm.sh,enroll-user.sh}
)
for role in broker edge; do
    reference_files+=("enrollment/quadlet/hosting-$role.container.d/50-enrollment.conf")
done
reference_files+=(keycloak/init-database.sh)
for path in "${reference_files[@]}"; do
    [[ -f "$repo/deploy/$path" && ! -L "$repo/deploy/$path" ]]
    mode=0644
    if [[ "$path" == keycloak/init-database.sh ]]; then mode=0755; fi
    install -m "$mode" "$repo/deploy/$path" "$bundle/$path"
done
cp "$repo/deploy/smoke-config.sh" "$bundle/"
cp "$repo/"{LICENSE,THIRD-PARTY-NOTICES.md} "$bundle/"
sed "s/:RELEASE_TAG$/:$tag/" "$repo/deploy/release/issuer.compose.yaml" > "$bundle/fixture/compose.yaml"
sed "s/:RELEASE_TAG$/:$tag/" "$repo/deploy/release/hosting-issuer.container" > "$bundle/fixture/quadlet/hosting-issuer.container"
printf '%s\n' "$tag" > "$bundle/version"
git -C "$repo" rev-parse HEAD > "$bundle/revision"

refs=()
for target in control-plane mcp-host worker-host fixture-issuer; do
    ref="localhost/monstertacle-hosting-$target:$tag"
    "$engine" build --platform linux/amd64 --target "$target" --tag "$ref" "$repo"
    refs+=("$ref")
done

# Supply both engines a Docker archive with the same image names and IDs.
if [[ "$engine" == docker ]]; then
    docker image save "${refs[@]}"
else
    podman save --format docker-archive --multi-image-archive "${refs[@]}"
fi | gzip -n > "$output/$name-images.tar.gz"

# Containerd-backed Docker reports the manifest digest. Podman and classic
# Docker report the digest of the archived image configuration.
tar -xOzf "$output/$name-images.tar.gz" manifest.json > "$work/image-manifest.json"
for ref in "${refs[@]}"; do
    id=$("$engine" image inspect --format '{{.Id}}' "$ref")
    config=$(jq -er --arg ref "$ref" '.[] | select(.RepoTags | index($ref)) | .Config' "$work/image-manifest.json")
    config=${config##*/}
    config=${config%.json}
    [[ "$config" =~ ^[0-9a-f]{64}$ ]]
    printf '%s\tsha256:%s\tsha256:%s\n' "$ref" "${id#sha256:}" "$config"
done > "$bundle/image-ids.tsv"

tar -C "$work" -czf "$output/$name-bundle.tar.gz" "$name"
(
    cd -- "$output"
    sha256sum "$name-bundle.tar.gz" "$name-images.tar.gz" > SHA256SUMS
)
