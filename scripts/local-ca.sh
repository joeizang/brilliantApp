#!/usr/bin/env bash
# The household's own certificate authority and the server's HTTPS certificate (docs/https.md).
#
#   scripts/local-ca.sh [name-or-ip ...]   create the CA if there isn't one, then issue a new server certificate
#   scripts/local-ca.sh --check            report whether the issued certificate is present, valid and not about to expire
#
# The names and addresses are what devices will type to reach the server (192.168.1.20, nuc.local). A name a device uses that is not
# on the certificate is refused by that device. With none given, this machine's main LAN address and host name are used; either way
# localhost, 127.0.0.1, ::1 and 10.0.2.2 (the Android emulator's name for the host) are added.
#
# Where things go (override with the variables):
#   BRILLIANT_CA_DIR   ~/.brilliant/ca   the CA's private key and certificate. The key stays here, mode 600 in a mode 700 folder, and
#                                        is never copied anywhere: whoever holds it can make certificates your devices believe.
#   BRILLIANT_TLS_DIR  <repo>/tls        what the server container mounts (git-ignored): server.pem, server.key, and brilliant-ca.crt,
#                                        the public certificate you install on each device.
#   BRILLIANT_CERT_DAYS  825             how long a server certificate lasts. Renewing is this script again; devices need nothing.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CA_DIR="${BRILLIANT_CA_DIR:-$HOME/.brilliant/ca}"
TLS_DIR="${BRILLIANT_TLS_DIR:-$ROOT/tls}"
CERT_DAYS="${BRILLIANT_CERT_DAYS:-825}"
CA_DAYS=3650
WARN_DAYS=30

die() { echo "local-ca: $*" >&2; exit 1; }
command -v openssl >/dev/null || die "openssl is required (it ships with macOS; on Ubuntu: sudo apt install openssl)"

is_ip() { [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ || "$1" == *:* ]]; }

# This machine's main LAN address: the one its default route leaves from.
primary_ip() {
    if [ "$(uname)" = Darwin ]; then
        local iface
        iface="$(route -n get default 2>/dev/null | awk '/interface:/ {print $2}')"
        [ -n "$iface" ] && ipconfig getifaddr "$iface" 2>/dev/null || true
    else
        ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") print $(i + 1)}' | head -n 1 || true
    fi
}

check() {
    [ -f "$TLS_DIR/server.pem" ] && [ -f "$TLS_DIR/server.key" ] || die "no certificate in $TLS_DIR; run scripts/local-ca.sh"
    openssl x509 -in "$TLS_DIR/server.pem" -noout -subject -enddate
    openssl x509 -in "$TLS_DIR/server.pem" -noout -text | grep -A1 'Subject Alternative Name' | tail -n 1 | sed 's/^ */names: /'
    if [ -f "$TLS_DIR/brilliant-ca.crt" ]; then
        openssl verify -CAfile "$TLS_DIR/brilliant-ca.crt" "$TLS_DIR/server.pem" >/dev/null 2>&1 \
            || die "the certificate was not issued by $TLS_DIR/brilliant-ca.crt"
    fi
    openssl x509 -in "$TLS_DIR/server.pem" -noout -checkend 0 >/dev/null || die "the certificate has expired; run scripts/local-ca.sh"
    openssl x509 -in "$TLS_DIR/server.pem" -noout -checkend $((WARN_DAYS * 86400)) >/dev/null \
        || die "the certificate expires within $WARN_DAYS days; run scripts/local-ca.sh to renew it (devices need nothing)"
    echo "ok"
}

case "${1:-}" in
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    --check) check; exit 0 ;;
    -*) die "unknown option $1 (see --help)" ;;
esac

# --- names for the certificate ----------------------------------------------------------------------------------------------------
names=("$@")
if [ ${#names[@]} -eq 0 ]; then
    ip="$(primary_ip)"
    [ -n "$ip" ] && names+=("$ip")
    host="$(hostname -s 2>/dev/null || true)"
    [ -n "$host" ] && names+=("$host" "$host.local")
fi
names+=(localhost 127.0.0.1 ::1 10.0.2.2)

san=""
shown=()
for n in "${names[@]}"; do
    [[ "$n" =~ ^[A-Za-z0-9.:_-]+$ ]] || die "'$n' is not a host name or IP address"
    case " ${shown[*]:-} " in *" $n "*) continue ;; esac
    shown+=("$n")
    if is_ip "$n"; then entry="IP:$n"; else entry="DNS:$n"; fi
    san="${san:+$san,}$entry"
done

umask 077
mkdir -p "$CA_DIR"
chmod 700 "$CA_DIR"
work="$(mktemp -d "$CA_DIR/.work.XXXXXX")"
trap 'rm -rf "$work"' EXIT

# --- the certificate authority (once) ------------------------------------------------------------------------------------------------
if [ ! -f "$CA_DIR/ca.key" ] || [ ! -f "$CA_DIR/ca.crt" ]; then
    [ ! -f "$CA_DIR/ca.key" ] && [ ! -f "$CA_DIR/ca.crt" ] || die "$CA_DIR has only half of a CA (ca.key and ca.crt); restore the other or move both away to start again"
    echo "Creating the certificate authority in $CA_DIR"
    cat > "$work/ca.cnf" <<EOF
[req]
distinguished_name = dn
prompt = no
[dn]
CN = Brilliant household CA
[ca]
basicConstraints = critical, CA:TRUE, pathlen:0
keyUsage = critical, keyCertSign, cRLSign
subjectKeyIdentifier = hash
EOF
    openssl genrsa -out "$work/ca.key" 4096 2>/dev/null
    openssl req -new -key "$work/ca.key" -config "$work/ca.cnf" -out "$work/ca.csr"
    openssl x509 -req -in "$work/ca.csr" -signkey "$work/ca.key" -days "$CA_DAYS" -sha256 \
        -extfile "$work/ca.cnf" -extensions ca -out "$work/ca.crt" 2>/dev/null
    mv "$work/ca.key" "$CA_DIR/ca.key"
    mv "$work/ca.crt" "$CA_DIR/ca.crt"
fi

# --- the server certificate -----------------------------------------------------------------------------------------------------------
echo "Issuing a server certificate for: ${shown[*]}"
cat > "$work/server.cnf" <<EOF
[req]
distinguished_name = dn
prompt = no
[dn]
CN = Brilliant household server
[server]
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = $san
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid
EOF
openssl genrsa -out "$work/server.key" 2048 2>/dev/null
openssl req -new -key "$work/server.key" -config "$work/server.cnf" -out "$work/server.csr"
serial="0x01$(openssl rand -hex 15)"   # the leading 01 keeps it positive
openssl x509 -req -in "$work/server.csr" -CA "$CA_DIR/ca.crt" -CAkey "$CA_DIR/ca.key" -set_serial "$serial" -days "$CERT_DAYS" -sha256 \
    -extfile "$work/server.cnf" -extensions server -out "$work/server.pem" 2>/dev/null
openssl verify -CAfile "$CA_DIR/ca.crt" "$work/server.pem" >/dev/null || die "the new certificate does not verify against the CA"

# The container's user is not you, so the server's own key has to be readable by others. It only secures this server, and the CA's key,
# which can vouch for anything, is not here. Everything is put in place only now that all of it was made.
mkdir -p "$TLS_DIR"
chmod 755 "$TLS_DIR"
cp "$CA_DIR/ca.crt" "$TLS_DIR/brilliant-ca.crt"
cp "$work/server.pem" "$TLS_DIR/server.pem"
cp "$work/server.key" "$TLS_DIR/server.key"
chmod 644 "$TLS_DIR/brilliant-ca.crt" "$TLS_DIR/server.pem" "$TLS_DIR/server.key"

echo
echo "Server certificate: $TLS_DIR/server.pem (+ server.key), valid $CERT_DAYS days"
echo "Install this on every Mac and Android device:  $TLS_DIR/brilliant-ca.crt"
echo "Then (re)start the server so it loads it: docker compose up -d --force-recreate server   (docs/https.md)"
