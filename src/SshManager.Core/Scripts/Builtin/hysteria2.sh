#!/usr/bin/env bash
# @name Hysteria 2 (Docker) — Ubuntu / Debian / CentOS
# @name_en Hysteria 2 (Docker) — Ubuntu / Debian / CentOS
# @group VPN
# @os ubuntu,debian,centos,rhel
# @description Ставит Docker и официальный сервер Hysteria 2 (QUIC поверх UDP): быстрый на плохих каналах, маскируется под HTTP/3-сайт.
# @description self-signed — домен не нужен, сертификат создаётся на сервере и закрепляется в ссылке (pinSHA256);
# @description acme — свой домен и сертификат Let's Encrypt (нужен свободный TCP-порт 80). Открывает UDP-порт в firewall.
# @description Повторный запуск сохраняет пароль и сертификат, если не отмечено «Новый пароль и сертификат».
# @description_en Installs Docker and the official Hysteria 2 server (QUIC over UDP): fast on lossy links, looks like an HTTP/3 site.
# @description_en self-signed: no domain, the certificate is made on the server and pinned in the link (pinSHA256);
# @description_en acme: your own domain and a Let's Encrypt certificate (TCP port 80 must be free). Opens the UDP port in the firewall.
# @description_en Running it again keeps the password and certificate unless "New password and certificate" is checked.
#
# @param HY2_PORT number required default=443 label="UDP-порт" label_en="UDP port" hint="Может совпадать с TCP-портом VLESS: это разные протоколы" hint_en="May match the TCP port of VLESS: different protocols"
# @param HY2_TLS choice options=self-signed,acme default=self-signed label="Сертификат" label_en="Certificate"
# @param HY2_SNI text default=www.bing.com when=HY2_TLS=self-signed label="Имя в сертификате (SNI)" label_en="Name in the certificate (SNI)"
# @param HY2_DOMAIN text when=HY2_TLS=acme label="Домен сервера" label_en="Server domain" hint="A-запись должна указывать на этот сервер" hint_en="Its A record must point to this server"
# @param ACME_EMAIL text when=HY2_TLS=acme label="E-mail для Let's Encrypt" label_en="E-mail for Let's Encrypt" hint="Необязательно" hint_en="Optional"
# @param HY2_MASQUERADE text default=https://www.bing.com label="Сайт-маскировка" label_en="Masquerade site" hint="Что увидит браузер или сканер, открыв сервер по HTTP/3" hint_en="What a browser or scanner sees when it opens the server over HTTP/3"
# @param HY2_OBFS bool default=0 label="Обфускация Salamander" label_en="Salamander obfuscation" hint="Прячет QUIC от DPI, но тогда сервер не похож на сайт; клиент должен поддерживать obfs" hint_en="Hides QUIC from DPI, but then the server no longer looks like a site; the client must support obfs"
# @param HY2_PASSWORD secret label="Пароль" label_en="Password" hint="Пусто — оставить текущий или сгенерировать" hint_en="Empty = keep the current one or generate"
# @param SERVER_ADDRESS text label="Адрес сервера в ссылке" label_en="Server address in the link" hint="Пусто — внешний IP (для acme — домен)" hint_en="Empty = the public IP (the domain for acme)"
# @param LINK_NAME text label="Имя подключения в клиенте" label_en="Connection name in the client" hint="Пусто — имя сервера в SSH Manager" hint_en="Empty = the server name in SSH Manager"
# @param REGENERATE bool default=0 label="Новый пароль и сертификат" label_en="New password and certificate" hint="Старая ссылка перестанет работать" hint_en="The old link stops working"
#
# @result HY2_URL label="Ссылка Hysteria 2" label_en="Hysteria 2 link"
# @result HY2_PORT label="UDP-порт Hysteria 2" label_en="Hysteria 2 UDP port"
# @result HY2_TLS label="Сертификат Hysteria 2" label_en="Hysteria 2 certificate"
#
# Runs standalone too: sudo HY2_PORT=443 bash hysteria2.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

DIR=/opt/hysteria
IMAGE=tobyxdd/hysteria:v2
CONTAINER=hysteria2

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

# Ubuntu, Debian, CentOS Stream, Rocky, AlmaLinux, RHEL (8 or newer) or a derivative. Sets OS_FAMILY (debian | rhel),
# OS_MAJOR and DOCKER_REPO / DOCKER_CODENAME for download.docker.com
require_os(){
  [[ -r /etc/os-release ]] || die "/etc/os-release not found"
  . /etc/os-release
  OS_MAJOR="${VERSION_ID:-0}"; OS_MAJOR="${OS_MAJOR%%.*}"
  case " ${ID:-} ${ID_LIKE:-} " in
    *" ubuntu "*) OS_FAMILY=debian; DOCKER_REPO=ubuntu; DOCKER_CODENAME="${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}" ;;
    *" debian "*) OS_FAMILY=debian; DOCKER_REPO=debian; DOCKER_CODENAME="${VERSION_CODENAME:-}" ;;
    *" rhel "* | *" centos "*)
      OS_FAMILY=rhel; DOCKER_CODENAME=""
      if [[ "${ID:-}" == rhel ]]; then DOCKER_REPO=rhel; else DOCKER_REPO=centos; fi
      [[ "$OS_MAJOR" =~ ^[0-9]+$ ]] && (( OS_MAJOR >= 8 )) || die "CentOS / RHEL 8 or newer is required (found ${PRETTY_NAME:-unknown})" ;;
    *) die "Ubuntu, Debian or CentOS / Rocky / AlmaLinux / RHEL is required (found ${PRETTY_NAME:-unknown})" ;;
  esac
  if [[ "$OS_FAMILY" == debian ]]; then cmd apt-get || die "apt-get not found"; else cmd dnf || die "dnf not found"; fi
  echo "OS: ${PRETTY_NAME:-$ID}"
}

# EPEL (Extra Packages for Enterprise Linux) with CodeReady Builder, which some of its packages need
enable_epel(){
  rpm -q epel-release >/dev/null 2>&1 && return 0
  log "Enabling EPEL"
  dnf -y -q install epel-release >/dev/null 2>&1 ||
    dnf -y -q install "https://dl.fedoraproject.org/pub/epel/epel-release-latest-${OS_MAJOR}.noarch.rpm" || die "Could not enable EPEL"
  if cmd crb; then crb enable >/dev/null 2>&1 || true
  else dnf config-manager --set-enabled crb >/dev/null 2>&1 || dnf config-manager --set-enabled powertools >/dev/null 2>&1 || true
  fi
}

APT_UPDATED=0
# Installs the packages that are missing. Names are Debian's; on CentOS / RHEL they are mapped (iproute2 = iproute)
# and a package missing from the base repositories is taken from EPEL.
pkg_install(){
  local missing=() p
  if [[ "$OS_FAMILY" == rhel ]]; then
    for p in "$@"; do
      [[ "$p" == iproute2 ]] && p=iproute
      rpm -q --whatprovides "$p" >/dev/null 2>&1 || missing+=("$p") # curl-minimal provides curl
    done
    (( ${#missing[@]} )) || return 0
    echo "dnf: installing ${missing[*]}"
    dnf -y -q install "${missing[@]}" 2>/dev/null && return 0
    enable_epel
    dnf -y -q install "${missing[@]}"
    return
  fi
  for p in "$@"; do
    dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q "ok installed" || missing+=("$p")
  done
  (( ${#missing[@]} )) || return 0
  if (( ! APT_UPDATED )); then apt-get -o DPkg::Lock::Timeout=300 -o Acquire::Retries=3 update -qq; APT_UPDATED=1; fi
  echo "apt: installing ${missing[*]}"
  apt-get -o DPkg::Lock::Timeout=300 -o Acquire::Retries=3 install -y -qq --no-install-recommends "${missing[@]}"
}

pkg_installed(){ if [[ "$OS_FAMILY" == rhel ]]; then rpm -q "$1" >/dev/null 2>&1; else dpkg-query -W -f='${Status}' "$1" 2>/dev/null | grep -q "ok installed"; fi; }

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
  if [[ "$OS_FAMILY" == rhel ]]; then
    # also when "docker" is podman-docker: Docker CE replaces it and podman's runc/buildah (--allowerasing)
    log "Installing Docker Engine + compose plugin (download.docker.com)"
    [[ -f /etc/yum.repos.d/docker-ce.repo ]] ||
      curl -fsSL "https://download.docker.com/linux/${DOCKER_REPO}/docker-ce.repo" -o /etc/yum.repos.d/docker-ce.repo
    dnf -y -q install --allowerasing docker-ce docker-ce-cli containerd.io docker-compose-plugin
  elif cmd docker; then
    log "Docker is installed without the compose plugin: adding it"
    pkg_install docker-compose-plugin 2>/dev/null || pkg_install docker-compose-v2 || die "Install the docker compose plugin and run again"
  else
    log "Installing Docker Engine + compose plugin (download.docker.com)"
    [[ -n "${DOCKER_CODENAME}" ]] || die "Unknown distribution codename"
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL "https://download.docker.com/linux/${DOCKER_REPO}/gpg" -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${DOCKER_REPO} ${DOCKER_CODENAME} stable" \
      > /etc/apt/sources.list.d/docker.list
    APT_UPDATED=0
    pkg_install docker-ce docker-ce-cli containerd.io docker-compose-plugin
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

save_state(){
  umask 077
  printf 'HY2_PASSWORD=%q\nHY2_OBFS_PASSWORD=%q\nHY2_CERT_SNI=%q\n' "$HY2_PASSWORD" "$HY2_OBFS_PASSWORD" "${HY2_CERT_SNI:-}" > "$DIR/state.env"
  umask 022
}

make_cert(){
  if [[ -s "$DIR/certs/server.crt" && -s "$DIR/certs/server.key" && "${HY2_CERT_SNI:-}" == "$HY2_SNI" ]]; then
    echo "Keeping the certificate for ${HY2_SNI}"
  else
    log "Creating a self-signed certificate for ${HY2_SNI}"
    openssl req -x509 -nodes -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -days 3650 \
      -keyout "$DIR/certs/server.key" -out "$DIR/certs/server.crt" \
      -subj "/CN=${HY2_SNI}" -addext "subjectAltName=DNS:${HY2_SNI}" 2>/dev/null
    chmod 600 "$DIR/certs/server.key"
    HY2_CERT_SNI="$HY2_SNI"
  fi
  HY2_PIN="$(openssl x509 -noout -fingerprint -sha256 -in "$DIR/certs/server.crt" | cut -d= -f2 | tr -d ':' | tr 'A-F' 'a-f')"
}

tune_udp(){
  # bigger UDP buffers, as the Hysteria docs recommend for QUIC (minimal images may lack the folder)
  install -d -m 0755 /etc/sysctl.d
  cat > /etc/sysctl.d/99-hysteria.conf <<'EOF'
net.core.rmem_max=16777216
net.core.wmem_max=16777216
EOF
  sysctl -q -p /etc/sysctl.d/99-hysteria.conf 2>/dev/null || warn "Could not raise the UDP buffer sizes (container-based VPS?)"
}

write_config(){
  log "Writing $DIR"
  umask 077
  {
    echo "listen: :${HY2_PORT}"
    echo ""
    if [[ "$HY2_TLS" == acme ]]; then
      echo "acme:"
      echo "  domains:"
      echo "    - ${HY2_DOMAIN}"
      [[ -z "$ACME_EMAIL" ]] || echo "  email: ${ACME_EMAIL}"
      echo "  dir: /acme"
      echo "  type: http"
    else
      echo "tls:"
      echo "  cert: /etc/hysteria/certs/server.crt"
      echo "  key: /etc/hysteria/certs/server.key"
    fi
    echo ""
    echo "auth:"
    echo "  type: password"
    echo "  password: \"${HY2_PASSWORD}\""
    if [[ "$HY2_OBFS" == 1 ]]; then
      echo ""
      echo "obfs:"
      echo "  type: salamander"
      echo "  salamander:"
      echo "    password: \"${HY2_OBFS_PASSWORD}\""
    fi
    echo ""
    echo "masquerade:"
    echo "  type: proxy"
    echo "  proxy:"
    echo "    url: \"${HY2_MASQUERADE}\""
    echo "    rewriteHost: true"
  } > "$DIR/config.yaml"
  umask 022
  cat > "$DIR/docker-compose.yml" <<YAML
services:
  hysteria:
    image: ${IMAGE}
    container_name: ${CONTAINER}
    restart: unless-stopped
    user: "0:0"
    network_mode: host
    volumes:
      - ./config.yaml:/etc/hysteria.yaml:ro
      - ./certs:/etc/hysteria/certs:ro
      - ./acme:/acme
    command: ["server", "-c", "/etc/hysteria.yaml"]
    logging:
      driver: local
      options:
        max-size: "2m"
        max-file: "3"
YAML
}

start(){
  log "Starting Hysteria 2"
  pull "$IMAGE"
  (cd "$DIR" && docker compose up -d --force-recreate)
  # with acme the certificate is fetched first, which can take a while
  local i
  for (( i = 0; i < 90; i += 3 )); do
    [[ -n "$(port_owner "$HY2_PORT" udp)" ]] && break
    [[ "$(docker inspect --format '{{.RestartCount}}' "$CONTAINER")" == 0 ]] || break
    sleep 3
  done
  if [[ -z "$(port_owner "$HY2_PORT" udp)" || "$(docker inspect --format '{{.State.Status}} {{.RestartCount}}' "$CONTAINER")" != "running 0" ]]; then
    docker logs --tail 100 "$CONTAINER" || true
    die "Hysteria did not start (see the log above)"
  fi
}

main(){
  require_root
  require_os

  HY2_PORT="$(trim "${HY2_PORT:-443}")"
  HY2_TLS="$(trim "${HY2_TLS:-self-signed}")"; HY2_TLS="${HY2_TLS,,}"
  HY2_SNI="$(trim "${HY2_SNI:-www.bing.com}")"; HY2_SNI="${HY2_SNI,,}"
  HY2_DOMAIN="$(trim "${HY2_DOMAIN:-}")"; HY2_DOMAIN="${HY2_DOMAIN,,}"
  ACME_EMAIL="$(trim "${ACME_EMAIL:-}")"
  HY2_MASQUERADE="$(trim "${HY2_MASQUERADE:-https://www.bing.com}")"
  HY2_OBFS="${HY2_OBFS:-0}"
  SERVER_ADDRESS="$(trim "${SERVER_ADDRESS:-}")"
  LINK_NAME="$(trim "${LINK_NAME:-}")"
  local new_password="${HY2_PASSWORD:-}"

  [[ "$HY2_PORT" =~ ^[0-9]+$ ]] && (( HY2_PORT >= 1 && HY2_PORT <= 65535 )) || die "Invalid port: $HY2_PORT"
  [[ "$HY2_TLS" == self-signed || "$HY2_TLS" == acme ]] || die "HY2_TLS must be self-signed or acme"
  [[ "$HY2_MASQUERADE" =~ ^https?://[^[:space:]\"\\]+$ ]] || die "The masquerade site must be an http(s):// URL"
  [[ "$new_password" != *[\"\\]* ]] || die "The password must not contain quotes or backslashes"
  if [[ "$HY2_TLS" == acme ]]; then
    valid_domain "$HY2_DOMAIN" || die "Set the server domain for acme (e.g. vpn.example.com)"
  else
    valid_domain "$HY2_SNI" || die "Invalid certificate name: $HY2_SNI"
  fi

  pkg_install ca-certificates curl openssl iproute2
  ensure_docker
  install -d -m 0700 "$DIR" "$DIR/certs" "$DIR/acme"

  HY2_PASSWORD="" HY2_OBFS_PASSWORD="" HY2_CERT_SNI=""
  if [[ "${REGENERATE:-0}" == 1 ]]; then
    echo "New password and certificate requested"
    rm -f "$DIR/state.env" "$DIR/certs/server.crt" "$DIR/certs/server.key"
  elif [[ -f "$DIR/state.env" ]]; then
    . "$DIR/state.env"
  fi
  [[ -z "$new_password" ]] || HY2_PASSWORD="$new_password"
  [[ -n "$HY2_PASSWORD" ]] || HY2_PASSWORD="$(rand_pass 24)"
  [[ -n "$HY2_OBFS_PASSWORD" ]] || HY2_OBFS_PASSWORD="$(rand_pass 24)"

  compose_down "$DIR"
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  require_free_port "$HY2_PORT" udp

  HY2_PIN=""
  if [[ "$HY2_TLS" == acme ]]; then
    check_dns "$HY2_DOMAIN"
    require_free_port 80 tcp
    open_port 80 tcp
  else
    make_cert
  fi
  save_state
  tune_udp
  write_config
  open_port "$HY2_PORT" udp
  start

  local address="$SERVER_ADDRESS" name="${LINK_NAME:-${SSHM_SERVER_NAME:-hysteria2}}" query link
  if [[ -z "$address" ]]; then
    if [[ "$HY2_TLS" == acme ]]; then address="$HY2_DOMAIN"; else address="$(public_ip)"; fi
  fi
  [[ -n "$address" ]] || address=YOUR_SERVER_IP
  if [[ "$HY2_TLS" == acme ]]; then
    query="sni=$(url_encode "$HY2_DOMAIN")"
  else
    query="sni=$(url_encode "$HY2_SNI")&insecure=1&pinSHA256=${HY2_PIN}"
  fi
  [[ "$HY2_OBFS" != 1 ]] || query+="&obfs=salamander&obfs-password=$(url_encode "$HY2_OBFS_PASSWORD")"
  link="hysteria2://$(url_encode "$HY2_PASSWORD")@${address}:${HY2_PORT}/?${query}#$(url_encode "$name")"

  echo ""
  echo "====================== HYSTERIA 2 ======================"
  echo "Address:   ${address}"
  echo "Port:      ${HY2_PORT} (udp)"
  echo "Password:  ${HY2_PASSWORD}"
  if [[ "$HY2_TLS" == acme ]]; then
    echo "TLS:       Let's Encrypt for ${HY2_DOMAIN} (renewed by Hysteria itself)"
  else
    echo "TLS:       self-signed, SNI ${HY2_SNI}, insecure=1"
    echo "pinSHA256: ${HY2_PIN}"
  fi
  [[ "$HY2_OBFS" != 1 ]] || echo "Obfs:      salamander, password ${HY2_OBFS_PASSWORD}"
  echo "Masquerade: ${HY2_MASQUERADE}"
  echo ""
  echo "Link:"
  echo "$link"
  echo "========================================================"
  echo "Clients: Hiddify, NekoBox, v2rayN / v2rayNG, sing-box, Shadowrocket, Streisand, the official Hysteria app."
  echo "UDP must reach the server: some hosting providers and networks throttle or block it."
  echo "Logs: docker logs --tail 100 -f ${CONTAINER}"

  sshm_result HY2_URL "$link"
  sshm_result HY2_PORT "$HY2_PORT"
  sshm_result HY2_TLS "$HY2_TLS"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
