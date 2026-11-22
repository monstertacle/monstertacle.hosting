#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

fail() {
    printf 'Public ingress preflight: %s\n' "$1" >&2
    exit 1
}

if (( $# != 4 && $# != 6 )); then
    fail "Usage: $0 HOSTNAME IPV4 EDGE_PORT ISSUER [DNS_SERVER DNS_PORT]"
fi
hostname=$1
ipv4=$2
port=$3
issuer=$4
label='[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?'
[[ ${#hostname} -le 253 && $hostname =~ ^($label\.)+$label$ ]] || fail "Invalid DNS hostname."
[[ $ipv4 =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || fail "Invalid IPv4 address."
[[ $port =~ ^[1-9][0-9]{3,4}$ ]] || fail "Use a high edge port."
(( port >= 1024 && port <= 65535 )) || fail "Use a high edge port."
[[ $issuer == https://?* ]] || fail "Use the exact HTTPS issuer."
interface=$(ip -4 -o address show to "$ipv4/32") || fail "Cannot inspect local addresses."
[[ -n $interface ]] || fail "The expected IPv4 address is absent from the host."

dns=(dig +time=3 +tries=1 +noall +comments +answer)
if (( $# == 6 )); then
    [[ $5 =~ ^[0-9a-fA-F.:]+$ && $6 =~ ^[1-9][0-9]{0,4}$ ]] || fail "Invalid resolver address or port."
    dns+=("@$5" -p "$6")
fi
dns_records() {
    local reply
    reply=$("${dns[@]}" "$hostname." "$1") || return 1
    [[ $reply == *'status: NOERROR,'* ]] || return 1
    awk '!/^;/ && NF { print tolower($1), $3, $4, $5 }' <<< "$reply"
}

a=$(dns_records A) || fail "DNS A lookup failed."
aaaa=$(dns_records AAAA) || fail "DNS AAAA lookup failed."
[[ $a == "$hostname. IN A $ipv4" && -z $aaaa ]] || fail "DNS must resolve directly and exclusively to the expected IPv4 address."

listeners=$(ss -Hln -t "sport = :$port") || fail "Cannot inspect edge listeners."
publication=$(awk '{ print $4 }' <<< "$listeners")
[[ $publication == "127.0.0.1:$port" ]] || fail "The edge port must have exactly one IPv4 loopback listener."

ca=()
curl_ca=()
if [[ -n ${SSL_CERT_FILE:-} ]]; then
    ca=(-CAfile "$SSL_CERT_FILE" -no-CApath -no-CAstore)
    curl_ca=(--cacert "$SSL_CERT_FILE")
fi
peer=$(timeout 8s openssl s_client -connect "127.0.0.1:$port" -servername "$hostname" \
    -verify_hostname "$hostname" -verify_return_error "${ca[@]}" </dev/null 2>/dev/null) ||
    fail "Kestrel TLS or certificate trust verification failed."
sans=$(openssl x509 -noout -ext subjectAltName <<< "$peer") || fail "Cannot read the Kestrel certificate SAN."
tr ',' '\n' <<< "$sans" | awk -v san="DNS:$hostname" '$1 == san { found=1 } END { exit !found }' ||
    fail "Kestrel must serve a certificate with the exact DNS SAN."
openssl x509 -noout -checkend 86400 <<< "$peer" >/dev/null ||
    fail "The Kestrel certificate must be valid for at least 24 more hours."

origin="https://$hostname"
request=(curl -q --silent --show-error --noproxy '*' --proto '=https' --max-time 18
    --connect-to "$hostname:443:127.0.0.1:$port" "${curl_ca[@]}")
status=$("${request[@]}" -o /dev/null -w '%{http_code}' "$origin/.well-known/hosting-ingress-readiness") ||
    fail "Cannot reach edge readiness over TLS."
[[ $status == 204 ]] || fail "Public OAuth readiness is disabled or its prerequisites failed."

metadata=$("${request[@]}" --fail "$origin/.well-known/oauth-protected-resource/mcp") ||
    fail "Cannot read protected resource metadata."
jq -e --arg resource "$origin/mcp" --arg issuer "$issuer" \
    '.resource == $resource and .authorization_servers == [$issuer]' <<< "$metadata" >/dev/null ||
    fail "The running edge does not advertise the expected resource and issuer."

status=$("${request[@]}" -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' \
    -d '{}' "$origin/mcp") || fail "Cannot verify the MCP authentication boundary."
[[ $status == 401 ]] || fail "Anonymous MCP must return HTTP 401."
printf 'Public ingress preflight passed.\n'
