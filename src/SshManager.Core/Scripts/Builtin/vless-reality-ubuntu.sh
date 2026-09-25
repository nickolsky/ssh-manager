#!/usr/bin/env bash
# @name VLESS REALITY (Xray в Docker) — Ubuntu
# @name_en VLESS REALITY (Xray in Docker) — Ubuntu
# @group VPN
# @os ubuntu
# @description Ставит Docker и Xray (VLESS + REALITY), открывает порт в firewall и выдаёт ссылку для клиента.
# @description Повторный запуск генерирует новые ключи — ссылку в клиентах нужно обновить.
# @description_en Installs Docker and Xray (VLESS + REALITY), opens the port in the firewall and returns a client link.
# @description_en Running it again generates new keys: update the link in your clients.
#
# @param REALITY_DOMAIN text required label="Домен для маскировки (SNI)" label_en="Domain to mimic (SNI)" default=dl.google.com hint="Сайт, под который маскируется трафик REALITY" hint_en="Site whose TLS the REALITY traffic imitates"
# @param XRAY_TRANSPORT choice options=xhttp,tcp default=xhttp label="Транспорт" label_en="Transport" hint="xhttp — новый режим; tcp — старый, с flow=xtls-rprx-vision (для клиентов без XHTTP)" hint_en="xhttp is the new mode; tcp is the old one with flow=xtls-rprx-vision (for clients without XHTTP)"
# @param XRAY_PORT number required default=443 label="Порт Xray" label_en="Xray port"
# @param XHTTP_PATH text default=/xhttp when=XRAY_TRANSPORT=xhttp label="Путь XHTTP" label_en="XHTTP path"
# @param SERVER_ADDRESS text label="Адрес сервера в ссылке" label_en="Server address in the link" hint="Пусто — внешний IP сервера (api.ipify.org)" hint_en="Empty = the server's public IP (api.ipify.org)"
# @param LINK_NAME text label="Имя подключения в клиенте" label_en="Connection name in the client" hint="Пусто — имя сервера в SSH Manager" hint_en="Empty = the server name in SSH Manager"
#
# @result VLESS_URL label="Ссылка VLESS" label_en="VLESS link"
# @result XRAY_PORT label="Порт Xray" label_en="Xray port" monitor=Xray
# @result XRAY_TRANSPORT label="Транспорт Xray" label_en="Xray transport"
# @result XRAY_PUBKEY label="REALITY PublicKey" label_en="REALITY public key"
#
# Adapted from github.com/nickolsky/vless_docker_install_scripts for SSH Manager:
# parameters come as environment variables (the interactive prompts are skipped when they are set),
# results are written as KEY=value lines to $SSHM_RESULT. Runs standalone as before.
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

log(){ echo -e "\n== $* =="; }
die(){ echo "ERROR: $*" >&2; exit 1; }
cmd(){ command -v "$1" >/dev/null 2>&1; }

# SSH Manager: report a value back (saved to the server's attributes); no-op when run by hand
sshm_result(){ [[ -n "${SSHM_RESULT:-}" ]] && printf '%s=%s\n' "$1" "$2" >> "$SSHM_RESULT"; return 0; }

require_root(){ [[ "${EUID}" -eq 0 ]] || die "Run as root: sudo ./install.sh"; }

prompt_var() {
  local var="$1" text="$2" def="${3:-}"
  local cur="${!var:-}"
  [[ -n "$cur" ]] && return 0
  if [[ -t 0 ]]; then
    local input=""
    read -r -p "${text}${def:+ [${def}]}: " input
    [[ -z "$input" ]] && printf -v "$var" "%s" "$def" || printf -v "$var" "%s" "$input"
  else
    printf -v "$var" "%s" "$def"
    echo "No TTY detected; using default ${var}='${!var}'" >&2
  fi
}

trim_value() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf "%s" "$value"
}

url_encode() {
  local value="$1" out="" i ch
  for (( i=0; i<${#value}; i++ )); do
    ch="${value:i:1}"
    case "$ch" in
      [a-zA-Z0-9.~_-]) out+="$ch" ;;
      *) printf -v out '%s%%%02X' "$out" "'$ch" ;;
    esac
  done
  printf "%s" "$out"
}

ensure_prereqs(){
  log "Ensuring prerequisites"
  # minimal images may lack these; install only what is missing (the key is used as .asc, no gpg needed)
  local missing=() p
  for p in curl ca-certificates openssl iproute2; do
    dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q "ok installed" || missing+=("$p")
  done
  if (( ${#missing[@]} )); then
    apt-get -o DPkg::Lock::Timeout=300 update -y
    apt-get -o DPkg::Lock::Timeout=300 install -y --no-install-recommends "${missing[@]}"
  fi
}

install_docker_ubuntu() {
  log "Installing Docker Engine + compose plugin"
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc

  local codename; codename="$(. /etc/os-release && echo "${UBUNTU_CODENAME:-${VERSION_CODENAME:-noble}}")"
  cat > /etc/apt/sources.list.d/docker.list <<EOF
deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${codename} stable
EOF

  apt-get update -y
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
}

ensure_docker(){
  if ! cmd docker; then install_docker_ubuntu; fi
  systemctl enable --now docker >/dev/null 2>&1 || true
  docker compose version >/dev/null 2>&1 || install_docker_ubuntu
}

docker_pull_or_die() {
  local img="$1"
  log "Pulling image: $img"
  timeout 240s docker pull "$img" >/dev/null || die "Failed to pull $img"
}

# --- FIREWALL (SSH-SAFE) ---

ensure_ssh_safe_ufw() {
  # Only if ufw exists; do NOT remove anything; only allow ssh then enable (if user has ufw installed)
  if cmd ufw; then
    ufw allow 22/tcp >/dev/null 2>&1 || true
    ufw allow OpenSSH >/dev/null 2>&1 || true
    # If ufw is inactive, enabling can lock you out unless ssh allowed (we ensure it above)
    if ufw status | grep -qi inactive; then
      ufw --force enable >/dev/null 2>&1 || true
    fi
  fi
}

open_firewall_port() {
  local port="$1" proto="${2:-tcp}"

  # UFW
  if cmd ufw; then
    ufw allow "${port}/${proto}" >/dev/null 2>&1 || true
    echo "Firewall: opened ${port}/${proto} via ufw"
    return 0
  fi

  # firewalld
  if cmd firewall-cmd && systemctl is-active --quiet firewalld; then
    firewall-cmd --zone=public --add-port="${port}/${proto}" >/dev/null 2>&1 || true
    firewall-cmd --zone=public --add-port="${port}/${proto}" --permanent >/dev/null 2>&1 || true
    firewall-cmd --reload >/dev/null 2>&1 || true
    echo "Firewall: opened ${port}/${proto} via firewalld"
    return 0
  fi

  # iptables fallback
  if cmd iptables; then
    iptables -I INPUT -p "${proto}" --dport "${port}" -j ACCEPT >/dev/null 2>&1 || true
    echo "Firewall: opened ${port}/${proto} via iptables (non-persistent)"
    return 0
  fi

  echo "WARNING: No firewall tool found; open ${port}/${proto} manually."
}

is_port_in_use(){
  local port="$1"
  ss -lntp 2>/dev/null | awk '{print $4}' | grep -qE ":${port}$"
}

cleanup_legacy_web(){
  log "Removing legacy fake web containers"
  if [[ -d /opt/nginx ]]; then
    (cd /opt/nginx && docker compose down) >/dev/null 2>&1 || true
  fi
  docker rm -f nginx-web certbot >/dev/null 2>&1 || true
}

# --- XRAY REALITY CREDS (supports old+new x25519 output) ---

gen_xray_secrets() {
  local img="ghcr.io/xtls/xray-core:latest"
  echo "== Pulling official Xray image for REALITY key generation: ${img} =="
  docker_pull_or_die "$img"

  echo "== Generating UUID =="
  XRAY_UUID="$(docker run --rm "$img" uuid 2>&1 | tr -d '\r' | head -n1)"
  [[ -n "$XRAY_UUID" ]] || die "Failed to generate UUID"

  echo "== Generating REALITY x25519 keypair =="
  local xout
  xout="$(docker run --rm "$img" x25519 2>&1 | tr -d '\r')"

  # Robust parsing for Private Key and Public Key across Xray output variants.
  # Recent images may emit:
  #   PrivateKey: <key>
  #   PublicKey: <key>
  #   Password (PublicKey): <key>
  XRAY_PRIVKEY="$(echo "$xout" | sed -n 's/^Private[ ]\?key:[ ]*//Ip' | head -n1)"
  XRAY_PUBKEY="$(echo "$xout" | sed -n 's/^\(Public[ ]\?key\|Password\([ ]*(PublicKey)\)\?\):[ ]*//Ip' | head -n1)"

  # Clean up any trailing/leading whitespace
  XRAY_PRIVKEY="$(echo "$XRAY_PRIVKEY" | xargs)"
  XRAY_PUBKEY="$(echo "$XRAY_PUBKEY" | xargs)"

  # Final sanity: must look like base64url-ish token
  if [[ -z "$XRAY_PRIVKEY" || -z "$XRAY_PUBKEY" ]]; then
    echo "Failed to parse x25519 output:"
    echo "---- Raw output ----"
    echo "$xout"
    echo "-------------------"
    die "x25519 parse failed"
  fi
  if ! echo "$XRAY_PRIVKEY" | grep -Eq '^[A-Za-z0-9_-]{43,}$'; then
    echo "Parsed private key looks invalid: $XRAY_PRIVKEY"
    die "Invalid private key format"
  fi
  if ! echo "$XRAY_PUBKEY" | grep -Eq '^[A-Za-z0-9_-]{43,}$'; then
    echo "Parsed public key/password looks invalid: $XRAY_PUBKEY"
    die "Invalid public key format"
  fi

  XRAY_SHORTID="$(openssl rand -hex 8)"
  [[ -n "$XRAY_SHORTID" ]] || die "Failed to generate shortId"
}

write_xray(){
  log "Writing /opt/xray"
  mkdir -p /opt/xray

  cat > /opt/xray/docker-compose.yml <<'YAML'
services:
  xray:
    image: ghcr.io/xtls/xray-core:latest
    container_name: xray-reality
    restart: unless-stopped
    user: "0:0"
    cap_add:
      - NET_BIND_SERVICE
    network_mode: host
    volumes:
      - ./config.json:/etc/xray/config.json:ro
    command: run -config /etc/xray/config.json
    environment:
      - XRAY_LOG_LEVEL=info
YAML

  local client_json transport_extra_json
  if [[ "${XRAY_TRANSPORT}" == "tcp" ]]; then
    client_json="{ \"id\": \"${XRAY_UUID}\", \"flow\": \"xtls-rprx-vision\", \"email\": \"user@xray\" }"
    transport_extra_json=""
  else
    client_json="{ \"id\": \"${XRAY_UUID}\", \"email\": \"user@xray\" }"
    transport_extra_json=$(cat <<JSON
,
        "xhttpSettings": {
          "path": "${XHTTP_PATH}",
          "mode": "auto"
        }
JSON
)
  fi

  cat > /opt/xray/config.json <<JSON
{
  "log": { "loglevel": "info" },
  "inbounds": [
    {
      "tag": "reality-in",
      "port": ${XRAY_PORT},
      "listen": "0.0.0.0",
      "protocol": "vless",
      "settings": {
        "clients": [
          ${client_json}
        ],
        "decryption": "none"
      },
      "streamSettings": {
        "network": "${XRAY_TRANSPORT}",
        "security": "reality"${transport_extra_json},
        "realitySettings": {
          "show": false,
          "dest": "${REALITY_DOMAIN}:443",
          "xver": 0,
          "serverNames": [ "${REALITY_DOMAIN}" ],
          "privateKey": "${XRAY_PRIVKEY}",
          "shortIds": [ "${XRAY_SHORTID}", "" ],
          "spiderX": "/"
        }
      }
    }
  ],
  "outbounds": [
    { "tag": "direct", "protocol": "freedom", "settings": {} },
    { "tag": "block", "protocol": "blackhole", "settings": {} }
  ],
  "routing": {
    "domainStrategy": "AsIs",
    "rules": [
      { "type": "field", "inboundTag": [ "reality-in" ], "outboundTag": "direct" }
    ]
  }
}
JSON
}
start_xray(){
  log "Starting Xray"
  docker_pull_or_die "ghcr.io/xtls/xray-core:latest"

  # Clean old container if exists (host network can keep port busy)
  (cd /opt/xray && docker compose down) >/dev/null 2>&1 || true
  docker rm -f xray-reality >/dev/null 2>&1 || true

  (cd /opt/xray && docker compose up -d --force-recreate)
  sleep 2

  if docker ps --format '{{.Names}} {{.Status}}' | grep -q '^xray-reality .*Restarting'; then
    docker logs --tail=150 xray-reality || true
    die "xray-reality is restarting (see logs above)"
  fi
}

get_public_ip(){ curl -fsSL https://api.ipify.org 2>/dev/null || true; }

print_out(){
  local ip; ip="$(get_public_ip)"
  local address="${SERVER_ADDRESS:-${ip:-YOUR_SERVER_IP}}"
  local flow_value="<empty>" vless_link

  if [[ "${XRAY_TRANSPORT}" == "tcp" ]]; then
    flow_value="xtls-rprx-vision"
    vless_link="vless://${XRAY_UUID}@${address}:${XRAY_PORT}?encryption=none&security=reality&sni=$(url_encode "${REALITY_DOMAIN}")&fp=edge&pbk=$(url_encode "${XRAY_PUBKEY}")&sid=$(url_encode "${XRAY_SHORTID}")&spx=$(url_encode "/")&type=tcp&flow=xtls-rprx-vision#$(url_encode "${LINK_NAME}")"
  else
    vless_link="vless://${XRAY_UUID}@${address}:${XRAY_PORT}?encryption=none&security=reality&sni=$(url_encode "${REALITY_DOMAIN}")&fp=edge&pbk=$(url_encode "${XRAY_PUBKEY}")&sid=$(url_encode "${XRAY_SHORTID}")&spx=$(url_encode "/")&type=xhttp&host=$(url_encode "${REALITY_DOMAIN}")&path=$(url_encode "${XHTTP_PATH}")&mode=auto#$(url_encode "${LINK_NAME}")"
  fi

  echo ""
  echo "==================== XRAY REALITY VLESS ===================="
  echo "1. Address:    ${address}"
  echo "2. Port:       ${XRAY_PORT}"
  echo "3. ID (UUID):  ${XRAY_UUID}"
  echo "4. Flow:       ${flow_value}"
  echo "5. Encryption: none"
  echo "6. Transport:  ${XRAY_TRANSPORT}"
  echo "7. Security:   reality"
  echo "8. SNI:        ${REALITY_DOMAIN}"
  echo "9. Fingerprint: edge (uTLS)"
  echo "10. PublicKey: ${XRAY_PUBKEY}"
  echo "11. ShortID:   ${XRAY_SHORTID} (or leave blank)"
  echo "12. SpiderX:   /"
  if [[ "${XRAY_TRANSPORT}" == "xhttp" ]]; then
    echo "13. XHTTP path:${XHTTP_PATH}"
    echo "14. XHTTP mode:auto"
  fi
  echo ""
  echo "VLESS URL:"
  echo "${vless_link}"

  sshm_result VLESS_URL "${vless_link}"
  sshm_result XRAY_PORT "${XRAY_PORT}"
  sshm_result XRAY_TRANSPORT "${XRAY_TRANSPORT}"
  sshm_result XRAY_PUBKEY "${XRAY_PUBKEY}"
  echo "============================================================"
  echo ""
  echo "Troubleshooting:"
  if [[ "${XRAY_TRANSPORT}" == "tcp" ]]; then
    echo "- IMPORTANT: In v2rayN, set 'Flow' to 'xtls-rprx-vision' and transport to 'tcp'."
  else
    echo "- IMPORTANT: In v2rayN, leave 'Flow' empty, set transport to 'xhttp', and set XHTTP path to '${XHTTP_PATH}'."
  fi
  echo "- Set 'Fingerprint' to 'edge' and 'SpiderX' to '/' if your client exposes those fields."
  echo "- Ensure your client supports Xray REALITY (e.g., v2rayN 6.0+, v2rayNG 1.8+, Nekoray 3.0+)."
  echo "- If connection still fails, try changing the mimic domain (REALITY_DOMAIN) to 'dl.google.com'."
  echo ""
}
main(){
  require_root
  ensure_prereqs
  ensure_docker

  prompt_var REALITY_DOMAIN "Enter REALITY domain to mimic (e.g., dl.google.com)" "dl.google.com"
  prompt_var XRAY_TRANSPORT "Enter Xray transport (xhttp/tcp)" "xhttp"
  prompt_var XRAY_PORT      "Enter XRAY listen port" "443"

  REALITY_DOMAIN="$(trim_value "${REALITY_DOMAIN}")"
  XRAY_TRANSPORT="$(trim_value "${XRAY_TRANSPORT}")"
  XRAY_TRANSPORT="${XRAY_TRANSPORT,,}"
  XRAY_PORT="$(trim_value "${XRAY_PORT}")"

  [[ -n "${REALITY_DOMAIN}" ]] || die "REALITY_DOMAIN cannot be empty"
  [[ "${XRAY_TRANSPORT}" == "xhttp" || "${XRAY_TRANSPORT}" == "tcp" ]] || die "XRAY_TRANSPORT must be 'xhttp' or 'tcp'"
  [[ "${XRAY_PORT}" =~ ^[0-9]+$ ]] || die "Invalid XRAY_PORT"
  (( XRAY_PORT >= 1 && XRAY_PORT <= 65535 )) || die "XRAY_PORT out of range"

  if [[ "${XRAY_TRANSPORT}" == "xhttp" ]]; then
    prompt_var XHTTP_PATH "Enter XHTTP path" "/xhttp"
    XHTTP_PATH="$(trim_value "${XHTTP_PATH}")"
    [[ -n "${XHTTP_PATH}" ]] || die "XHTTP_PATH cannot be empty"
    [[ "${XHTTP_PATH}" == /* ]] || XHTTP_PATH="/${XHTTP_PATH}"
    [[ "${XHTTP_PATH}" != *\"* && "${XHTTP_PATH}" != *\\* && "${XHTTP_PATH}" != *" "* ]] || die "XHTTP_PATH must not contain spaces, quotes, or backslashes"
  else
    XHTTP_PATH=""
  fi

  SERVER_ADDRESS="$(trim_value "${SERVER_ADDRESS:-}")"
  LINK_NAME="$(trim_value "${LINK_NAME:-}")"
  [[ -n "${LINK_NAME}" ]] || LINK_NAME="${SSHM_SERVER_NAME:-xray-reality-${XRAY_TRANSPORT}}"

  echo "Using XRAY_TRANSPORT=${XRAY_TRANSPORT}"
  echo "Using XRAY_PORT=${XRAY_PORT}"
  [[ "${XRAY_TRANSPORT}" == "xhttp" ]] && echo "Using XHTTP_PATH=${XHTTP_PATH}"

  # Firewall: keep SSH safe, then open ports
  ensure_ssh_safe_ufw
  open_firewall_port 22 tcp
  open_firewall_port "${XRAY_PORT}" tcp

  cleanup_legacy_web
  gen_xray_secrets
  write_xray
  start_xray

  print_out

  log "Status"
  docker ps --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}' | sed 's/\t/  /g'
  echo ""
  echo "Logs:"
  echo "  docker logs -f xray-reality"
}

main "$@"
