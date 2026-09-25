#!/usr/bin/env bash
# @name AmneziaWG (Docker) — Ubuntu / Debian
# @name_en AmneziaWG (Docker) — Ubuntu / Debian
# @group VPN
# @os ubuntu,debian
# @description Ставит Docker и сервер AmneziaWG (WireGuard с обфускацией против DPI) из официального образа amneziavpn/amneziawg-go.
# @description Модуль ядра не нужен (работает в userspace), подходит для VPS с 512 МБ памяти.
# @description Каждому устройству — свой клиент (один конфиг нельзя использовать на двух устройствах сразу): для каждого
# @description выдаётся ключ vpn:// для Amnezia VPN (в результатах сервера, там же QR-код для AmneziaWG и Amnezia VPN)
# @description и файл .conf в /opt/amneziawg/clients (скачать можно через «Файлы»).
# @description Повторный запуск сохраняет ключи: новое имя в списке добавляет клиента, убранное — удаляет его.
# @description_en Installs Docker and an AmneziaWG server (WireGuard with anti-DPI obfuscation) from the official amneziavpn/amneziawg-go image.
# @description_en No kernel module needed (runs in userspace), fits a VPS with 512 MB of memory.
# @description_en One client per device (a config cannot be used on two devices at once): each gets a vpn:// key for
# @description_en Amnezia VPN (in the server's results, with a QR code for AmneziaWG and Amnezia VPN there too)
# @description_en and a .conf file in /opt/amneziawg/clients (download it with "Files").
# @description_en Running it again keeps the keys: a new name in the list adds a client, a removed one deletes it.
#
# @param AWG_CLIENTS text required default=phone label="Клиенты (устройства)" label_en="Clients (devices)" hint="Имена через запятую: phone,laptop,tablet (латиница, цифры, - и _). Убранное из списка имя удаляется с сервера" hint_en="Names separated by commas: phone,laptop,tablet (latin letters, digits, - and _). A name removed from the list is deleted from the server"
# @param AWG_PORT number label="UDP-порт" label_en="UDP port" hint="Пусто — случайный (запоминается при повторных запусках)" hint_en="Empty = a random one (kept on later runs)"
# @param AWG_DNS text default=1.1.1.1,1.0.0.1 label="DNS для клиентов" label_en="DNS for clients"
# @param SERVER_ADDRESS text label="Адрес сервера в конфигах" label_en="Server address in the configs" hint="Пусто — внешний IP сервера" hint_en="Empty = the server's public IP"
# @param REGENERATE bool default=0 label="Новые ключи и параметры обфускации" label_en="New keys and obfuscation settings" hint="Все клиенты создаются заново, старые конфиги перестанут работать" hint_en="All clients are recreated, the old configs stop working"
#
# @result AWG_KEY_* label="Amnezia VPN" label_en="Amnezia VPN"
# @result AWG_PORT label="UDP-порт AmneziaWG" label_en="AmneziaWG UDP port"
# @result AWG_CLIENTS_DIR label="Конфиги клиентов AmneziaWG" label_en="AmneziaWG client configs"
# @result AWG_CLIENT_COUNT label="Клиентов AmneziaWG" label_en="AmneziaWG clients"
#
# Runs standalone too: sudo AWG_CLIENTS=phone,laptop bash amneziawg.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

DIR=/opt/amneziawg
IMAGE=amneziavpn/amneziawg-go:latest
CONTAINER=amneziawg
NET=10.66.66
CONF=/etc/amnezia/amneziawg/awg0.conf

# ---- shared helpers: base (identical in every built-in script that has them; edit all copies) ----
# bash 5.2 reads "&" in a ${var//a/b} replacement as the matched text; the helpers here mean it literally
shopt -u patsub_replacement 2>/dev/null || true

log(){ echo -e "\n== $* =="; }
warn(){ echo "WARNING: $*" >&2; }
die(){ echo "ERROR: $*" >&2; exit 1; }
cmd(){ command -v "$1" >/dev/null 2>&1; }
# SSH Manager: report a value back (saved to the server's attributes); no-op when run by hand
sshm_result(){ [[ -n "${SSHM_RESULT:-}" ]] && printf '%s=%s\n' "$1" "$2" >> "$SSHM_RESULT"; return 0; }
trim(){ local v="$1"; v="${v#"${v%%[![:space:]]*}"}"; printf '%s' "${v%"${v##*[![:space:]]}"}"; }

url_encode(){
  local value="$1" out="" i ch
  for (( i=0; i<${#value}; i++ )); do
    ch="${value:i:1}"
    case "$ch" in
      [a-zA-Z0-9.~_-]) out+="$ch" ;;
      *) printf -v out '%s%%%02X' "$out" "'$ch" ;;
    esac
  done
  printf '%s' "$out"
}

require_root(){ [[ "${EUID}" -eq 0 ]] || die "Run as root: sudo bash $0"; }

# Ubuntu, Debian or a derivative; sets DOCKER_REPO / DOCKER_CODENAME for download.docker.com
require_debian_family(){
  [[ -r /etc/os-release ]] || die "/etc/os-release not found"
  . /etc/os-release
  case " ${ID:-} ${ID_LIKE:-} " in
    *" ubuntu "*) DOCKER_REPO=ubuntu; DOCKER_CODENAME="${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}" ;;
    *" debian "*) DOCKER_REPO=debian; DOCKER_CODENAME="${VERSION_CODENAME:-}" ;;
    *) die "Ubuntu or Debian is required (found ${PRETTY_NAME:-unknown})" ;;
  esac
  cmd apt-get || die "apt-get not found"
  echo "OS: ${PRETTY_NAME:-$ID}"
}

APT_UPDATED=0
apt_install(){ # installs the packages that are missing
  local missing=() p
  for p in "$@"; do
    dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q "ok installed" || missing+=("$p")
  done
  (( ${#missing[@]} )) || return 0
  if (( ! APT_UPDATED )); then apt-get -o DPkg::Lock::Timeout=300 -o Acquire::Retries=3 update -qq; APT_UPDATED=1; fi
  echo "apt: installing ${missing[*]}"
  apt-get -o DPkg::Lock::Timeout=300 -o Acquire::Retries=3 install -y -qq --no-install-recommends "${missing[@]}"
}

# docker pull with retries: registries time out now and then
pull(){
  local img i
  for img in "$@"; do
    for i in 1 2 3 4 5; do
      docker pull -q "$img" >/dev/null && continue 2
      echo "docker pull $img failed (attempt $i of 5), retrying" >&2
      sleep $(( i * 5 ))
    done
    die "Could not pull $img: no access to the registry?"
  done
}

compose_pull(){ # project folder
  local i
  for i in 1 2 3 4 5; do
    (cd "$1" && docker compose pull -q) && return 0
    echo "docker compose pull failed (attempt $i of 5), retrying" >&2
    sleep $(( i * 5 ))
  done
  die "Could not pull the images: no access to the registry?"
}

ensure_docker(){
  if cmd docker && docker compose version >/dev/null 2>&1; then
    systemctl enable --now docker >/dev/null 2>&1 || true
    return 0
  fi
  if cmd docker; then
    log "Docker is installed without the compose plugin: adding it"
    apt_install docker-compose-plugin 2>/dev/null || apt_install docker-compose-v2 || die "Install the docker compose plugin and run again"
  else
    log "Installing Docker Engine + compose plugin (download.docker.com)"
    [[ -n "${DOCKER_CODENAME}" ]] || die "Unknown distribution codename"
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL "https://download.docker.com/linux/${DOCKER_REPO}/gpg" -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${DOCKER_REPO} ${DOCKER_CODENAME} stable" \
      > /etc/apt/sources.list.d/docker.list
    APT_UPDATED=0
    apt_install docker-ce docker-ce-cli containerd.io docker-compose-plugin
  fi
  systemctl enable --now docker >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true
  docker compose version >/dev/null 2>&1 || die "docker compose is not available"
}

# Opens a port only when a firewall is actually filtering (never enables one)
open_port(){
  local port="$1" proto="${2:-tcp}"
  if cmd ufw && ufw status 2>/dev/null | grep -qi '^Status: active'; then
    ufw allow "${port}/${proto}" >/dev/null && echo "Firewall (ufw): opened ${port}/${proto}"
  elif cmd firewall-cmd && systemctl is-active --quiet firewalld 2>/dev/null; then
    firewall-cmd -q --add-port="${port}/${proto}" && firewall-cmd -q --permanent --add-port="${port}/${proto}" &&
      echo "Firewall (firewalld): opened ${port}/${proto}"
  elif cmd iptables && iptables -S INPUT 2>/dev/null | grep -q '^-P INPUT DROP'; then
    iptables -C INPUT -p "$proto" --dport "$port" -j ACCEPT 2>/dev/null || iptables -I INPUT -p "$proto" --dport "$port" -j ACCEPT
    echo "Firewall (iptables): opened ${port}/${proto} (not persistent)"
  fi
  return 0
}

# The process listening on a port, empty when it is free
port_owner(){
  local flag=-lntp; [[ "${2:-tcp}" == udp ]] && flag=-lnup
  ss "$flag" 2>/dev/null | awk -v p=":$1" 'NR > 1 && substr($4, length($4) - length(p) + 1) == p { print $4, $NF; exit }'
}
require_free_port(){
  local owner; owner="$(port_owner "$1" "${2:-tcp}")"
  [[ -z "$owner" ]] || die "Port $1/${2:-tcp} is already in use: ${owner}. Choose another port or stop that service."
}

public_ip(){
  curl -fsS4 --max-time 10 https://api.ipify.org 2>/dev/null || curl -fsS4 --max-time 10 https://ifconfig.me 2>/dev/null ||
    hostname -I 2>/dev/null | awk '{print $1}'
}
# ---- end of shared helpers: base ----

# ---- shared helpers: services (identical in every built-in script that has them; edit all copies) ----
rand_pass(){ local s; s="$(openssl rand -base64 64 | tr -dc 'A-Za-z0-9')"; printf '%s' "${s:0:${1:-24}}"; }

# NAME=value line for a compose .env file: single quotes are literal; double quotes when the value has one
dotenv(){
  local v="$2"
  if [[ "$v" == *"'"* ]]; then
    v="${v//\\/\\\\}"; v="${v//\"/\\\"}"; v="${v//\$/\$\$}"
    printf '%s="%s"\n' "$1" "$v"
  else
    printf "%s='%s'\n" "$1" "$v"
  fi
}

valid_domain(){ [[ "$1" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]]; }

# Stops when the domain does not resolve, warns when it points somewhere else
check_dns(){
  local resolved ip
  resolved="$(getent ahostsv4 "$1" 2>/dev/null | awk '{print $1; exit}' || true)"
  ip="$(public_ip)"
  [[ -n "$resolved" ]] || die "$1 does not resolve. Create an A record pointing to ${ip:-this server} first."
  [[ "$resolved" == "$ip" ]] || warn "$1 resolves to ${resolved}, this server looks like ${ip}; getting a certificate may fail"
  return 0
}

# RAM + swap below $1 MiB: a warning; free space on /var below $2 MiB: an error
check_resources(){
  local ram swap disk
  ram="$(awk '/^MemTotal/ {print int($2/1024)}' /proc/meminfo)"
  swap="$(awk '/^SwapTotal/ {print int($2/1024)}' /proc/meminfo)"
  disk="$(df -Pm /var | awk 'END {print $4}')"
  echo "RAM ${ram} MiB, swap ${swap} MiB, free on /var ${disk} MiB"
  (( ram + swap >= $1 )) || warn "Only $((ram + swap)) MiB of RAM + swap; $1 MiB or more is recommended"
  (( disk >= $2 )) || die "Need at least $2 MiB free on /var, found ${disk} MiB"
  return 0
}

# A swap file for servers with less than 2 GiB of RAM and no swap
ensure_swap(){ # size in MiB
  local ram; ram="$(awk '/^MemTotal/ {print int($2/1024)}' /proc/meminfo)"
  (( ram < 2048 )) || return 0
  [[ -z "$(swapon --noheadings 2>/dev/null)" ]] || return 0
  [[ ! -e /swapfile ]] || { warn "/swapfile exists but is not in use; leaving it alone"; return 0; }
  log "Adding a $1 MiB swap file (/swapfile): the server has ${ram} MiB of RAM"
  fallocate -l "${1}M" /swapfile 2>/dev/null || dd if=/dev/zero of=/swapfile bs=1M count="$1" status=none
  chmod 600 /swapfile
  mkswap /swapfile >/dev/null
  if swapon /swapfile 2>/dev/null; then
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  else
    warn "Could not turn on swap (a container-based VPS?); continuing without it"
    rm -f /swapfile
  fi
}

# Waits until the URL answers with any HTTP status
wait_http(){ # url seconds
  local i
  for (( i = 0; i < $2; i += 5 )); do
    curl -s -o /dev/null --max-time 5 "$1" && return 0
    sleep 5
  done
  return 1
}

compose_down(){
  [[ -f "$1/docker-compose.yml" ]] || return 0
  (cd "$1" && docker compose down --remove-orphans >/dev/null 2>&1) || true
}

# Caddyfile for automatic HTTPS: domain, upstream (host:port), extra directives
write_caddyfile(){
  {
    if [[ -n "${ACME_EMAIL:-}" ]]; then printf '{\n\temail %s\n}\n\n' "$ACME_EMAIL"; fi
    printf '%s {\n\tencode zstd gzip\n%s\treverse_proxy %s\n}\n' "$1" "${3:-}" "$2"
  } > "$DIR/Caddyfile"
}

# The caddy service for docker-compose.yml (ports 80/443, certificates kept in ./caddy)
caddy_service(){
  cat <<'YAML'
  caddy:
    image: caddy:2-alpine
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
      - "443:443/udp"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - ./caddy/data:/data
      - ./caddy/config:/config
    logging:
      driver: local
      options:
        max-size: "2m"
        max-file: "3"
YAML
}
# ---- end of shared helpers: services ----

rand_range(){ # lo hi
  local n; n="$(od -An -N4 -tu4 /dev/urandom | tr -d ' ')"
  echo $(( $1 + n % ($2 - $1 + 1) ))
}

# "private public preshared" per line, made by awg inside the image
gen_keys(){
  docker run --rm --entrypoint sh "$IMAGE" -c \
    'i=0; while [ $i -lt '"$1"' ]; do k=$(awg genkey); echo "$k $(echo "$k" | awg pubkey) $(awg genpsk)"; i=$((i+1)); done'
}

new_server(){
  log "Generating the server key and obfuscation settings"
  local keys; keys="$(gen_keys 1)"
  SERVER_PRIV="${keys%% *}"; keys="${keys#* }"; SERVER_PUB="${keys%% *}"
  [[ -n "$AWG_PORT" ]] || AWG_PORT="$(rand_range 20000 60000)"
  # AmneziaWG 1.x parameters (understood by every AmneziaWG / Amnezia VPN client)
  JC="$(rand_range 4 8)"; JMIN=40; JMAX=70
  S1="$(rand_range 15 150)"
  while :; do S2="$(rand_range 15 150)"; (( S1 + 56 != S2 )) && break; done
  H1="$(rand_range 5 2147483647)"
  while :; do H2="$(rand_range 5 2147483647)"; (( H2 != H1 )) && break; done
  while :; do H3="$(rand_range 5 2147483647)"; (( H3 != H1 && H3 != H2 )) && break; done
  while :; do H4="$(rand_range 5 2147483647)"; (( H4 != H1 && H4 != H2 && H4 != H3 )) && break; done
}

save_state(){
  umask 077
  local v
  for v in SERVER_PRIV SERVER_PUB AWG_PORT JC JMIN JMAX S1 S2 H1 H2 H3 H4; do printf '%s=%q\n' "$v" "${!v}"; done > "$DIR/state.env"
  umask 022
}

obfuscation(){ printf 'Jc = %s\nJmin = %s\nJmax = %s\nS1 = %s\nS2 = %s\nH1 = %s\nH2 = %s\nH3 = %s\nH4 = %s\n' "$JC" "$JMIN" "$JMAX" "$S1" "$S2" "$H1" "$H2" "$H3" "$H4"; }

# Brings the clients in $DIR/clients (<name>.env with keys and address) in line with the list of names
sync_clients(){
  local f name keep n
  for f in "$DIR"/clients/*.env; do
    [[ -e "$f" ]] || continue
    name="$(basename "$f" .env)"
    keep=0
    for n in "${NAMES[@]}"; do [[ "$n" == "$name" ]] && keep=1; done
    if (( ! keep )); then
      echo "Removing client ${name} (not in the list any more)"
      rm -f "$DIR/clients/${name}.env" "$DIR/clients/${name}.conf"
    fi
  done

  local missing=()
  for n in "${NAMES[@]}"; do [[ -e "$DIR/clients/${n}.env" ]] || missing+=("$n"); done
  (( ${#missing[@]} )) || return 0
  log "Creating client(s): ${missing[*]}"
  local priv pub psk ip used i=0
  while read -r priv pub psk; do
    n="${missing[$i]}"; i=$((i + 1))
    # the lowest free address in the subnet
    ip=2
    while :; do
      used=0
      for f in "$DIR"/clients/*.env; do [[ -e "$f" ]] && grep -q "^IP=${NET}.${ip}\$" "$f" && used=1; done
      (( used )) || break
      ip=$((ip + 1))
    done
    (( ip < 255 )) || die "No free addresses left in ${NET}.0/24"
    printf 'PRIV=%s\nPUB=%s\nPSK=%s\nIP=%s\n' "$priv" "$pub" "$psk" "${NET}.${ip}" > "$DIR/clients/${n}.env"
    chmod 600 "$DIR/clients/${n}.env"
  done < <(gen_keys "${#missing[@]}")
  for n in "${missing[@]}"; do [[ -e "$DIR/clients/${n}.env" ]] || die "Could not create the keys for ${n}"; done
}

write_configs(){
  log "Writing $DIR"
  umask 077
  local f n PRIV PUB PSK IP
  {
    echo "[Interface]"
    echo "PrivateKey = ${SERVER_PRIV}"
    echo "Address = ${NET}.1/24"
    echo "ListenPort = ${AWG_PORT}"
    obfuscation
    echo "PostUp = iptables -t nat -A POSTROUTING -s ${NET}.0/24 -o eth0 -j MASQUERADE; iptables -A FORWARD -i %i -j ACCEPT; iptables -A FORWARD -o %i -j ACCEPT"
    echo "PostDown = iptables -t nat -D POSTROUTING -s ${NET}.0/24 -o eth0 -j MASQUERADE; iptables -D FORWARD -i %i -j ACCEPT; iptables -D FORWARD -o %i -j ACCEPT"
    for n in "${NAMES[@]}"; do
      . "$DIR/clients/${n}.env"
      echo ""
      echo "[Peer]"
      echo "# ${n}"
      echo "PublicKey = ${PUB}"
      echo "PresharedKey = ${PSK}"
      echo "AllowedIPs = ${IP}/32"
    done
  } > "$DIR/config/awg0.conf"

  for n in "${NAMES[@]}"; do
    . "$DIR/clients/${n}.env"
    {
      echo "[Interface]"
      echo "PrivateKey = ${PRIV}"
      echo "Address = ${IP}/32"
      echo "DNS = ${AWG_DNS}"
      echo "MTU = 1280"
      obfuscation
      echo ""
      echo "[Peer]"
      echo "PublicKey = ${SERVER_PUB}"
      echo "PresharedKey = ${PSK}"
      echo "Endpoint = ${ENDPOINT_HOST}:${AWG_PORT}"
      echo "AllowedIPs = 0.0.0.0/0, ::/0"
      echo "PersistentKeepalive = 25"
    } > "$DIR/clients/${n}.conf"
  done
  umask 022

  cat > "$DIR/docker-compose.yml" <<YAML
services:
  amneziawg:
    image: ${IMAGE}
    container_name: ${CONTAINER}
    restart: unless-stopped
    cap_add:
      - NET_ADMIN
    devices:
      - /dev/net/tun:/dev/net/tun
    sysctls:
      - net.ipv4.ip_forward=1
      - net.ipv4.conf.all.src_valid_mark=1
    ports:
      - "${AWG_PORT}:${AWG_PORT}/udp"
    volumes:
      - ./config:/etc/amnezia/amneziawg
    # awg-quick falls back to the userspace amneziawg-go when there is no kernel module
    command: ["/bin/sh", "-c", "awg-quick up ${CONF} && trap 'awg-quick down ${CONF}; exit 0' TERM INT; while :; do sleep 3600 & wait \$\$!; done"]
    logging:
      driver: local
      options:
        max-size: "2m"
        max-file: "3"
YAML
}

# ---------- Amnezia VPN key (vpn://) ----------

json_str(){
  local s="$1"
  s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; s="${s//$'\r'/}"; s="${s//$'\n'/\\n}"; s="${s//$'\t'/\\t}"
  printf '"%s"' "$s"
}

# shellcheck disable=SC2059 # the format is the bytes themselves
be32(){ printf "$(printf '\\x%02x\\x%02x\\x%02x\\x%02x' $(( $1 >> 24 & 255 )) $(( $1 >> 16 & 255 )) $(( $1 >> 8 & 255 )) $(( $1 & 255 )))"; }

# Qt's qCompress() of a file: its length (4 bytes, big-endian) and a zlib stream, made here from gzip's deflate data
# plus the zlib header and the Adler-32 checksum (no python or zlib tools needed on the server)
qcompress(){
  local len a=1 b=0 x
  len="$(stat -c %s "$1")"
  for x in $(od -An -v -tu1 "$1"); do a=$(( (a + x) % 65521 )); b=$(( (b + a) % 65521 )); done
  be32 "$len"
  printf '\x78\xda'
  gzip -9 -n -c "$1" | tail -c +11 | head -c -8
  be32 $(( (b << 16) | a ))
}

# The key Amnezia VPN imports from text: the same server JSON it builds itself when it imports a .conf file
amnezia_key(){ # client name
  local n="$1" PRIV PUB PSK IP host dns1 dns2 last json tmp
  . "$DIR/clients/${n}.env"
  host="${ENDPOINT_HOST#[}"; host="${host%]}"
  dns1="${AWG_DNS%%,*}"; dns2="${AWG_DNS#*, }"; [[ "$dns2" != "$AWG_DNS" ]] || dns2="$dns1"; dns2="${dns2%%,*}"
  last="{\"config\":$(json_str "$(cat "$DIR/clients/${n}.conf")"),\"hostName\":$(json_str "$host"),\"port\":${AWG_PORT}"
  last+=",\"client_priv_key\":$(json_str "$PRIV"),\"client_ip\":$(json_str "${IP}/32"),\"psk_key\":$(json_str "$PSK")"
  last+=",\"server_pub_key\":$(json_str "$SERVER_PUB"),\"mtu\":\"1280\",\"persistent_keep_alive\":\"25\""
  last+=",\"allowed_ips\":[\"0.0.0.0/0\",\"::/0\"]"
  last+=",\"Jc\":\"${JC}\",\"Jmin\":\"${JMIN}\",\"Jmax\":\"${JMAX}\",\"S1\":\"${S1}\",\"S2\":\"${S2}\""
  last+=",\"H1\":\"${H1}\",\"H2\":\"${H2}\",\"H3\":\"${H3}\",\"H4\":\"${H4}\"}"
  json="{\"containers\":[{\"container\":\"amnezia-awg\",\"awg\":{\"last_config\":$(json_str "$last")"
  json+=",\"isThirdPartyConfig\":true,\"port\":\"${AWG_PORT}\",\"transport_proto\":\"udp\"}}]"
  json+=",\"defaultContainer\":\"amnezia-awg\",\"description\":$(json_str "${SSHM_SERVER_NAME:-AmneziaWG} — ${n}")"
  json+=",\"dns1\":$(json_str "$dns1"),\"dns2\":$(json_str "$dns2"),\"hostName\":$(json_str "$host")}"
  tmp="$(mktemp)"
  printf '%s' "$json" > "$tmp"
  printf 'vpn://%s' "$(qcompress "$tmp" | base64 -w0 | tr '+/' '-_' | tr -d '=')"
  rm -f "$tmp"
}

start(){
  log "Starting AmneziaWG"
  (cd "$DIR" && docker compose up -d --force-recreate)
  local i
  for (( i = 0; i < 30; i += 2 )); do
    docker exec "$CONTAINER" awg show awg0 listen-port >/dev/null 2>&1 && break
    sleep 2
  done
  if ! docker exec "$CONTAINER" awg show awg0 listen-port >/dev/null 2>&1; then
    docker logs --tail 100 "$CONTAINER" || true
    die "The awg0 interface did not come up (see the log above)"
  fi
  if docker exec "$CONTAINER" sh -c 'ip -d link show awg0' 2>/dev/null | grep -q amneziawg; then
    echo "Using the AmneziaWG kernel module"
  else
    echo "Using the userspace implementation (amneziawg-go)"
  fi
}

main(){
  require_root
  require_debian_family

  AWG_PORT="$(trim "${AWG_PORT:-}")"
  AWG_DNS="$(trim "${AWG_DNS:-1.1.1.1,1.0.0.1}")"; AWG_DNS="${AWG_DNS// /}"; AWG_DNS="${AWG_DNS//,/, }"
  SERVER_ADDRESS="$(trim "${SERVER_ADDRESS:-}")"
  local wanted_port="$AWG_PORT" raw n seen=" "
  NAMES=()
  IFS=',' read -r -a raw <<< "${AWG_CLIENTS:-phone}"
  for n in "${raw[@]}"; do
    n="$(trim "$n")"
    [[ -n "$n" ]] || continue
    [[ "$n" =~ ^[A-Za-z0-9_-]{1,32}$ ]] || die "Client name '${n}': use latin letters, digits, - and _ (up to 32)"
    local k="${n,,}"; k="${k//-/_}"
    [[ "$seen" != *" ${k} "* ]] || die "Client name '${n}' is repeated (names differing only in case or - / _ clash)"
    seen+="${k} "
    NAMES+=("$n")
  done
  (( ${#NAMES[@]} >= 1 && ${#NAMES[@]} <= 250 )) || die "Give 1..250 client names"

  [[ -z "$AWG_PORT" ]] || { [[ "$AWG_PORT" =~ ^[0-9]+$ ]] && (( AWG_PORT >= 1 && AWG_PORT <= 65535 )); } || die "Invalid port: $AWG_PORT"
  [[ "$AWG_DNS" =~ ^[0-9a-fA-F:.,\ ]+$ ]] || die "DNS must be IP addresses separated by commas"
  [[ -c /dev/net/tun ]] || die "/dev/net/tun is missing: the VPS does not allow VPN interfaces (ask the provider to enable TUN)"

  apt_install ca-certificates curl openssl iproute2 qrencode gzip
  ensure_docker
  install -d -m 0700 "$DIR" "$DIR/config" "$DIR/clients"
  pull "$IMAGE"

  if [[ "${REGENERATE:-0}" == 1 ]]; then
    echo "New keys requested: all clients are recreated"
    rm -f "$DIR/state.env" "$DIR"/clients/*.env "$DIR"/clients/*.conf
  fi
  if [[ -f "$DIR/state.env" ]]; then
    . "$DIR/state.env"
    echo "Keeping the server key and obfuscation settings"
    [[ -z "$wanted_port" ]] || AWG_PORT="$wanted_port"
  else
    new_server
  fi

  compose_down "$DIR"
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  require_free_port "$AWG_PORT" udp

  ENDPOINT_HOST="${SERVER_ADDRESS:-$(public_ip)}"
  [[ -n "$ENDPOINT_HOST" ]] || die "Could not find the public IP; set the server address"
  [[ "$ENDPOINT_HOST" != *:* || "$ENDPOINT_HOST" == \[* ]] || ENDPOINT_HOST="[${ENDPOINT_HOST}]"

  save_state
  sync_clients
  write_configs
  open_port "$AWG_PORT" udp
  start

  local key
  for n in "${NAMES[@]}"; do
    key="$(amnezia_key "$n")"
    echo ""
    echo "==================== ${n} ===================="
    echo "Amnezia VPN key (Amnezia VPN → Add → paste the key):"
    echo "$key"
    echo ""
    echo "Config for AmneziaWG ($DIR/clients/${n}.conf):"
    cat "$DIR/clients/${n}.conf"
    echo ""
    qrencode -t utf8 < "$DIR/clients/${n}.conf" || true
    sshm_result "AWG_KEY_${n//-/_}" "$key"
  done
  echo ""
  echo "Server: ${ENDPOINT_HOST}:${AWG_PORT}/udp, clients: ${NAMES[*]}; configs: $DIR/clients"
  echo "The QR code of a config works in both AmneziaWG and Amnezia VPN. Plain WireGuard apps cannot connect."
  echo "Status: docker exec ${CONTAINER} awg show"

  sshm_result AWG_PORT "$AWG_PORT"
  sshm_result AWG_CLIENTS_DIR "$DIR/clients"
  sshm_result AWG_CLIENT_COUNT "${#NAMES[@]}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
