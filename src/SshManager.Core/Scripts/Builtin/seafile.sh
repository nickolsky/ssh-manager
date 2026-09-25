#!/usr/bin/env bash
# @name Seafile (Docker) — Ubuntu / Debian / CentOS
# @name_en Seafile (Docker) — Ubuntu / Debian / CentOS
# @group Cloud
# @os ubuntu,debian,centos,rhel
# @description Облачное хранилище Seafile 13 Community: быстрая синхронизация файлов, клиенты для Windows, macOS, Linux, Android и iOS.
# @description Официальные образы Seafile + MariaDB + Redis в /opt/seafile. С доменом — HTTPS через Caddy (порты 80 и 443),
# @description без домена — HTTP по IP на выбранном порту. Нужно от 2 ГБ памяти (при меньшем объёме можно создать swap).
# @description Повторный запуск обновляет образы внутри версии 13; пароли базы сохраняются.
# @description_en Seafile 13 Community cloud storage: fast file sync, clients for Windows, macOS, Linux, Android and iOS.
# @description_en The official Seafile images + MariaDB + Redis in /opt/seafile. With a domain: HTTPS through Caddy (ports 80 and 443);
# @description_en without one: HTTP by IP on the chosen port. Needs 2 GB of memory or more (a swap file can be added).
# @description_en Running it again updates the images within version 13; database passwords are kept.
#
# @param SF_DOMAIN text label="Домен" label_en="Domain" hint="С доменом — HTTPS на портах 80/443; пусто — HTTP по IP" hint_en="With a domain: HTTPS on ports 80/443; empty: HTTP by IP"
# @param SF_PORT number default=8000 label="HTTP-порт (без домена)" label_en="HTTP port (no domain)" hint="Используется, только если домен не задан" hint_en="Used only when there is no domain"
# @param ACME_EMAIL text label="E-mail для Let's Encrypt" label_en="E-mail for Let's Encrypt" hint="Необязательно, только с доменом" hint_en="Optional, only with a domain"
# @param SF_ADMIN_EMAIL text required default=admin@example.com label="E-mail администратора (логин)" label_en="Admin e-mail (login)"
# @param SF_ADMIN_PASSWORD secret label="Пароль администратора" label_en="Admin password" hint="Пусто — сгенерировать; задаётся только при первой установке" hint_en="Empty = generate; used on the first install only"
# @param SF_TIME_ZONE text default=Etc/UTC label="Часовой пояс" label_en="Time zone" hint="Например Europe/Moscow" hint_en="For example Europe/Berlin"
# @param ADD_SWAP bool default=1 label="Создать swap 2 ГБ, если памяти меньше 2 ГБ" label_en="Add 2 GB of swap when memory is below 2 GB"
#
# @result SF_URL label="Адрес Seafile" label_en="Seafile URL"
# @result SF_ADMIN_EMAIL label="Администратор Seafile" label_en="Seafile admin"
# @result SF_ADMIN_PASSWORD label="Пароль администратора Seafile" label_en="Seafile admin password"
# @result SF_PORT label="Порт Seafile" label_en="Seafile port" monitor=Seafile
#
# Runs standalone too: sudo SF_ADMIN_EMAIL=me@example.com bash seafile.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

DIR=/opt/seafile
SEAFILE_IMAGE=seafileltd/seafile-mc:13.0-latest

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

# A value kept in .env by an earlier run (empty on the first one)
env_value(){
  [[ -f "$DIR/.env" ]] || return 0
  sed -n "s/^$1='\(.*\)'\$/\1/p" "$DIR/.env" | sed -n 1p
}

write_files(){
  log "Writing $DIR"
  # SSHM_ names: the script's parameters are exported into the environment, and docker compose
  # prefers the environment over .env (an empty admin password parameter would win)
  umask 077
  {
    dotenv SSHM_DB_ROOT_PASSWORD "$DB_ROOT_PASSWORD"
    dotenv SSHM_DB_PASSWORD "$DB_PASSWORD"
    dotenv SSHM_REDIS_PASSWORD "$REDIS_PASSWORD"
    dotenv SSHM_JWT_PRIVATE_KEY "$JWT_PRIVATE_KEY"
    dotenv SSHM_SF_HOSTNAME "$SF_HOSTNAME"
    dotenv SSHM_SF_PROTOCOL "$SF_PROTOCOL"
    dotenv SSHM_SF_TIME_ZONE "$SF_TIME_ZONE"
    # only read by the first start (installation); cleared afterwards
    dotenv SSHM_SF_ADMIN_EMAIL "$SF_ADMIN_EMAIL"
    dotenv SSHM_SF_ADMIN_PASSWORD "${INSTALL_PASSWORD:-}"
  } > "$DIR/.env"
  umask 022

  local ports="" caddy=""
  if [[ -n "$SF_DOMAIN" ]]; then
    caddy="$(caddy_service)"
    write_caddyfile "$SF_DOMAIN" "seafile:80"
  else
    ports=$'    ports:\n      - "'"${SF_PORT}"$':80"'
  fi
  cat > "$DIR/docker-compose.yml" <<YAML
x-logging: &logging
  driver: local
  options:
    max-size: "2m"
    max-file: "3"

services:
  db:
    image: mariadb:10.11
    restart: unless-stopped
    environment:
      MYSQL_ROOT_PASSWORD: \${SSHM_DB_ROOT_PASSWORD}
      MYSQL_LOG_CONSOLE: "true"
      MARIADB_AUTO_UPGRADE: "1"
    volumes:
      - ./mysql:/var/lib/mysql
    healthcheck:
      test: ["CMD", "/usr/local/bin/healthcheck.sh", "--connect", "--mariadbupgrade", "--innodb_initialized"]
      interval: 20s
      start_period: 30s
      timeout: 5s
      retries: 10
    logging: *logging

  redis:
    image: redis:7-alpine
    restart: unless-stopped
    command: ["redis-server", "--requirepass", "\${SSHM_REDIS_PASSWORD}", "--save", "", "--appendonly", "no"]
    logging: *logging

  seafile:
    image: ${SEAFILE_IMAGE}
    restart: unless-stopped
${ports}
    volumes:
      - ./data:/shared
    environment:
      SEAFILE_MYSQL_DB_HOST: db
      SEAFILE_MYSQL_DB_PORT: "3306"
      SEAFILE_MYSQL_DB_USER: seafile
      SEAFILE_MYSQL_DB_PASSWORD: \${SSHM_DB_PASSWORD}
      INIT_SEAFILE_MYSQL_ROOT_PASSWORD: \${SSHM_DB_ROOT_PASSWORD}
      SEAFILE_MYSQL_DB_CCNET_DB_NAME: ccnet_db
      SEAFILE_MYSQL_DB_SEAFILE_DB_NAME: seafile_db
      SEAFILE_MYSQL_DB_SEAHUB_DB_NAME: seahub_db
      TIME_ZONE: \${SSHM_SF_TIME_ZONE}
      INIT_SEAFILE_ADMIN_EMAIL: \${SSHM_SF_ADMIN_EMAIL}
      INIT_SEAFILE_ADMIN_PASSWORD: \${SSHM_SF_ADMIN_PASSWORD}
      SEAFILE_SERVER_HOSTNAME: \${SSHM_SF_HOSTNAME}
      SEAFILE_SERVER_PROTOCOL: \${SSHM_SF_PROTOCOL}
      SITE_ROOT: /
      NON_ROOT: "false"
      JWT_PRIVATE_KEY: \${SSHM_JWT_PRIVATE_KEY}
      SEAFILE_LOG_TO_STDOUT: "true"
      ENABLE_GO_FILESERVER: "true"
      ENABLE_SEADOC: "false"
      CACHE_PROVIDER: redis
      REDIS_HOST: redis
      REDIS_PORT: "6379"
      REDIS_PASSWORD: \${SSHM_REDIS_PASSWORD}
      ENABLE_NOTIFICATION_SERVER: "false"
      ENABLE_SEAFILE_AI: "false"
      ENABLE_FACE_RECOGNITION: "false"
    depends_on:
      db:
        condition: service_healthy
      redis:
        condition: service_started
    logging: *logging
${caddy}
YAML
}

# Seahub answers once the first-time setup (databases, admin) is done; asked inside the container, because in
# domain mode Caddy answers port 80 with a redirect long before that, and nginx in the container says 502 meanwhile
wait_ready(){
  local i
  log "Waiting for Seafile to start (the first start sets up the databases, a few minutes)"
  for (( i = 0; i < 600; i += 10 )); do
    (cd "$DIR" && docker compose exec -T seafile curl -fs -o /dev/null http://localhost:80/ 2>/dev/null) && return 0
    (( i % 60 )) || echo "... still starting (${i}s)"
    sleep 10
  done
  (cd "$DIR" && docker compose logs --tail 80 seafile) || true
  die "Seafile did not start in 10 minutes (see the log above)"
}

main(){
  require_root
  require_os

  SF_DOMAIN="$(trim "${SF_DOMAIN:-}")"; SF_DOMAIN="${SF_DOMAIN,,}"
  SF_PORT="$(trim "${SF_PORT:-8000}")"
  ACME_EMAIL="$(trim "${ACME_EMAIL:-}")"
  SF_ADMIN_EMAIL="$(trim "${SF_ADMIN_EMAIL:-admin@example.com}")"
  SF_TIME_ZONE="$(trim "${SF_TIME_ZONE:-Etc/UTC}")"
  local new_password="${SF_ADMIN_PASSWORD:-}"

  [[ -z "$SF_DOMAIN" ]] || valid_domain "$SF_DOMAIN" || die "Invalid domain: $SF_DOMAIN"
  [[ -n "$SF_DOMAIN" ]] || { [[ "$SF_PORT" =~ ^[0-9]+$ ]] && (( SF_PORT >= 1 && SF_PORT <= 65535 )); } || die "Invalid port: $SF_PORT"
  [[ "$SF_ADMIN_EMAIL" =~ ^[^@[:space:]]+@[^@[:space:]]+$ ]] || die "The admin login must be an e-mail address"
  [[ -e "/usr/share/zoneinfo/${SF_TIME_ZONE}" || "$SF_TIME_ZONE" == Etc/UTC ]] || warn "Unknown time zone ${SF_TIME_ZONE}"
  [[ "$new_password" != *[$'\n\r']* ]] || die "Invalid password"

  pkg_install ca-certificates curl openssl iproute2
  [[ "${ADD_SWAP:-1}" != 1 ]] || ensure_swap 2048
  check_resources 1800 3000
  ensure_docker
  install -d -m 0755 "$DIR"

  DB_ROOT_PASSWORD="$(env_value SSHM_DB_ROOT_PASSWORD)"
  DB_PASSWORD="$(env_value SSHM_DB_PASSWORD)"
  REDIS_PASSWORD="$(env_value SSHM_REDIS_PASSWORD)"
  JWT_PRIVATE_KEY="$(env_value SSHM_JWT_PRIVATE_KEY)"
  [[ -n "$DB_ROOT_PASSWORD" ]] || DB_ROOT_PASSWORD="$(rand_pass 32)"
  [[ -n "$DB_PASSWORD" ]] || DB_PASSWORD="$(rand_pass 32)"
  [[ -n "$REDIS_PASSWORD" ]] || REDIS_PASSWORD="$(rand_pass 32)"
  [[ -n "$JWT_PRIVATE_KEY" ]] || JWT_PRIVATE_KEY="$(rand_pass 40)"

  local url generated="" first=0
  if [[ -n "$SF_DOMAIN" ]]; then
    SF_HOSTNAME="$SF_DOMAIN"; SF_PROTOCOL=https; url="https://${SF_DOMAIN}"
  else
    SF_HOSTNAME="$(public_ip)"; SF_PROTOCOL=http
    [[ "$SF_PORT" == 80 ]] || SF_HOSTNAME+=":${SF_PORT}"
    url="http://${SF_HOSTNAME}"
  fi

  INSTALL_PASSWORD=""
  if [[ ! -d "$DIR/data/seafile" ]]; then
    first=1
    INSTALL_PASSWORD="${new_password:-$(rand_pass 20)}"
    [[ -n "$new_password" ]] || generated="$INSTALL_PASSWORD"
  elif [[ -n "$new_password" ]]; then
    warn "Seafile is already installed: change the admin password in the web interface (the new one is ignored)"
  fi

  compose_down "$DIR"
  if [[ -n "$SF_DOMAIN" ]]; then
    check_dns "$SF_DOMAIN"
    require_free_port 80 tcp
    require_free_port 443 tcp
  else
    require_free_port "$SF_PORT" tcp
  fi

  write_files
  log "Starting Seafile"
  compose_pull "$DIR"
  (cd "$DIR" && docker compose up -d)
  wait_ready

  if (( first )); then
    # the admin password is not needed on disk (or in the container) after the installation
    INSTALL_PASSWORD=""
    write_files
    (cd "$DIR" && docker compose up -d)
    wait_ready >/dev/null
  fi

  if [[ -n "$SF_DOMAIN" ]]; then open_port 80 tcp; open_port 443 tcp; open_port 443 udp; else open_port "$SF_PORT" tcp; fi

  echo ""
  echo "Seafile: ${url}"
  echo "Admin: ${SF_ADMIN_EMAIL}${generated:+ / password ${generated}}"
  echo "Desktop and mobile clients: https://www.seafile.com/en/download/ (server address: ${url})"
  echo "Data: $DIR/data, database: $DIR/mysql; logs: cd $DIR && docker compose logs -f seafile"

  sshm_result SF_URL "$url"
  sshm_result SF_ADMIN_EMAIL "$SF_ADMIN_EMAIL"
  [[ -z "$generated" ]] || sshm_result SF_ADMIN_PASSWORD "$generated"
  if [[ -n "$SF_DOMAIN" ]]; then sshm_result SF_PORT 443; else sshm_result SF_PORT "$SF_PORT"; fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
