#!/usr/bin/env bash
# @name Nextcloud (Docker) — Ubuntu / Debian
# @name_en Nextcloud (Docker) — Ubuntu / Debian
# @group Cloud
# @os ubuntu,debian
# @description Облачное хранилище Nextcloud: официальный образ + PostgreSQL + Redis + фоновые задачи (cron), всё в /opt/nextcloud.
# @description С доменом — HTTPS через Caddy (сертификат Let's Encrypt, порты 80 и 443), без домена — HTTP по IP на выбранном порту.
# @description Нужно от 2 ГБ памяти (при меньшем объёме можно создать swap). Повторный запуск обновляет Nextcloud на следующую
# @description основную версию (по одной за раз, как требует Nextcloud), пароли базы сохраняются.
# @description_en Nextcloud cloud storage: the official image + PostgreSQL + Redis + background jobs (cron), all in /opt/nextcloud.
# @description_en With a domain: HTTPS through Caddy (Let's Encrypt, ports 80 and 443); without one: HTTP by IP on the chosen port.
# @description_en Needs 2 GB of memory or more (a swap file can be added on smaller servers). Running it again upgrades Nextcloud
# @description_en to the next major version (one at a time, as Nextcloud requires); database passwords are kept.
#
# @param NC_DOMAIN text label="Домен" label_en="Domain" hint="С доменом — HTTPS на портах 80/443; пусто — HTTP по IP" hint_en="With a domain: HTTPS on ports 80/443; empty: HTTP by IP"
# @param NC_PORT number default=8080 label="HTTP-порт (без домена)" label_en="HTTP port (no domain)" hint="Используется, только если домен не задан" hint_en="Used only when there is no domain"
# @param ACME_EMAIL text label="E-mail для Let's Encrypt" label_en="E-mail for Let's Encrypt" hint="Необязательно, только с доменом" hint_en="Optional, only with a domain"
# @param NC_ADMIN_USER text required default=admin label="Администратор" label_en="Admin user"
# @param NC_ADMIN_PASSWORD secret label="Пароль администратора" label_en="Admin password" hint="Пусто — сгенерировать при установке; при повторном запуске — задать новый" hint_en="Empty = generate on install; on a later run: set a new one"
# @param ADD_SWAP bool default=1 label="Создать swap 2 ГБ, если памяти меньше 2 ГБ" label_en="Add 2 GB of swap when memory is below 2 GB"
#
# @result NC_URL label="Адрес Nextcloud" label_en="Nextcloud URL"
# @result NC_ADMIN_USER label="Администратор Nextcloud" label_en="Nextcloud admin"
# @result NC_ADMIN_PASSWORD label="Пароль администратора Nextcloud" label_en="Nextcloud admin password"
# @result NC_PORT label="Порт Nextcloud" label_en="Nextcloud port" monitor=Nextcloud
#
# Runs standalone too: sudo NC_PORT=8080 bash nextcloud.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

DIR=/opt/nextcloud
NC_MAJOR=35   # newest major version this script installs

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

occ(){ (cd "$DIR" && docker compose exec -T -u www-data app php occ "$@"); }

installed_major(){
  [[ -f "$DIR/html/version.php" ]] || return 0
  sed -n 's/.*OC_Version *= *array *( *\([0-9]\+\).*/\1/p; s/.*OC_Version *= *\[ *\([0-9]\+\).*/\1/p' "$DIR/html/version.php" | sed -n 1p
}

is_installed(){ [[ -f "$DIR/html/config/config.php" ]] && grep -q "'installed' => true" "$DIR/html/config/config.php"; }

write_files(){ # image
  log "Writing $DIR"
  # SSHM_ names: the script's parameters are exported into the environment, and docker compose
  # prefers the environment over .env (an empty admin password parameter would win)
  umask 077
  {
    dotenv SSHM_POSTGRES_PASSWORD "$POSTGRES_PASSWORD"
    dotenv SSHM_REDIS_PASSWORD "$REDIS_PASSWORD"
    # only read by the first start (installation); cleared afterwards
    dotenv SSHM_NC_ADMIN_USER "$NC_ADMIN_USER"
    dotenv SSHM_NC_ADMIN_PASSWORD "${INSTALL_PASSWORD:-}"
  } > "$DIR/.env"
  umask 022

  local ports="" proxy="" caddy=""
  if [[ -n "$NC_DOMAIN" ]]; then
    proxy=$'      OVERWRITEPROTOCOL: https\n      TRUSTED_PROXIES: 172.16.0.0/12'
    caddy="$(caddy_service)"
    write_caddyfile "$NC_DOMAIN" "app:80" $'\theader Strict-Transport-Security "max-age=15552000;"\n\tredir /.well-known/carddav /remote.php/dav 301\n\tredir /.well-known/caldav /remote.php/dav 301\n'
  else
    ports=$'    ports:\n      - "'"${NC_PORT}"$':80"'
  fi
  cat > "$DIR/docker-compose.yml" <<YAML
x-logging: &logging
  driver: local
  options:
    max-size: "2m"
    max-file: "3"

services:
  db:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: nextcloud
      POSTGRES_USER: nextcloud
      POSTGRES_PASSWORD: \${SSHM_POSTGRES_PASSWORD}
    volumes:
      - ./db:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U nextcloud -d nextcloud"]
      interval: 10s
      timeout: 5s
      retries: 10
    logging: *logging

  redis:
    image: redis:7-alpine
    restart: unless-stopped
    command: ["redis-server", "--requirepass", "\${SSHM_REDIS_PASSWORD}", "--save", "", "--appendonly", "no"]
    logging: *logging

  app:
    image: $1
    restart: unless-stopped
    depends_on:
      db:
        condition: service_healthy
      redis:
        condition: service_started
${ports}
    volumes:
      - ./html:/var/www/html
    environment:
      POSTGRES_HOST: db
      POSTGRES_DB: nextcloud
      POSTGRES_USER: nextcloud
      POSTGRES_PASSWORD: \${SSHM_POSTGRES_PASSWORD}
      REDIS_HOST: redis
      REDIS_HOST_PASSWORD: \${SSHM_REDIS_PASSWORD}
      NEXTCLOUD_ADMIN_USER: \${SSHM_NC_ADMIN_USER}
      NEXTCLOUD_ADMIN_PASSWORD: \${SSHM_NC_ADMIN_PASSWORD}
      NEXTCLOUD_TRUSTED_DOMAINS: "${TRUSTED}"
      OVERWRITECLIURL: "${URL}"
      PHP_MEMORY_LIMIT: 512M
      PHP_UPLOAD_LIMIT: 16G
      APACHE_BODY_LIMIT: "0"
${proxy}
    logging: *logging

  cron:
    image: $1
    restart: unless-stopped
    entrypoint: /cron.sh
    depends_on:
      - app
    volumes:
      - ./html:/var/www/html
    logging: *logging
${caddy}
YAML
}

# The first start installs (or upgrades) Nextcloud, which takes a few minutes
wait_ready(){
  local i status
  log "Waiting for Nextcloud to install / upgrade (can take several minutes)"
  for (( i = 0; i < 900; i += 10 )); do
    status="$(occ status --output=json 2>/dev/null || true)"
    if [[ "$status" == *'"installed":true'* && "$status" == *'"maintenance":false'* && "$status" == *'"needsDbUpgrade":false'* ]]; then
      echo "$status"
      return 0
    fi
    (( i % 60 )) || echo "... still starting (${i}s)"
    sleep 10
  done
  (cd "$DIR" && docker compose logs --tail 80 app) || true
  die "Nextcloud did not get ready in 15 minutes (see the log above)"
}

main(){
  require_root
  require_debian_family

  NC_DOMAIN="$(trim "${NC_DOMAIN:-}")"; NC_DOMAIN="${NC_DOMAIN,,}"
  NC_PORT="$(trim "${NC_PORT:-8080}")"
  ACME_EMAIL="$(trim "${ACME_EMAIL:-}")"
  NC_ADMIN_USER="$(trim "${NC_ADMIN_USER:-admin}")"
  local new_password="${NC_ADMIN_PASSWORD:-}"

  [[ -z "$NC_DOMAIN" ]] || valid_domain "$NC_DOMAIN" || die "Invalid domain: $NC_DOMAIN"
  [[ -n "$NC_DOMAIN" ]] || { [[ "$NC_PORT" =~ ^[0-9]+$ ]] && (( NC_PORT >= 1 && NC_PORT <= 65535 )); } || die "Invalid port: $NC_PORT"
  [[ "$NC_ADMIN_USER" =~ ^[A-Za-z0-9._@-]+$ ]] || die "The admin name may contain letters, digits and . _ @ - only"
  [[ "$new_password" != *[$'\n\r']* ]] || die "Invalid password"

  apt_install ca-certificates curl openssl iproute2
  [[ "${ADD_SWAP:-1}" != 1 ]] || ensure_swap 2048
  check_resources 1800 3000
  ensure_docker
  install -d -m 0755 "$DIR"

  POSTGRES_PASSWORD="" REDIS_PASSWORD=""
  if [[ -f "$DIR/.env" ]]; then
    POSTGRES_PASSWORD="$(sed -n "s/^SSHM_POSTGRES_PASSWORD='\(.*\)'\$/\1/p" "$DIR/.env")"
    REDIS_PASSWORD="$(sed -n "s/^SSHM_REDIS_PASSWORD='\(.*\)'\$/\1/p" "$DIR/.env")"
  fi
  [[ -n "$POSTGRES_PASSWORD" ]] || POSTGRES_PASSWORD="$(rand_pass 32)"
  [[ -n "$REDIS_PASSWORD" ]] || REDIS_PASSWORD="$(rand_pass 32)"

  # one major version per run: Nextcloud cannot skip versions when upgrading
  local have target
  have="$(installed_major)"
  target="$NC_MAJOR"
  if [[ -n "$have" ]]; then
    (( have <= NC_MAJOR )) || die "Nextcloud ${have} is newer than this script knows (${NC_MAJOR}); nothing changed"
    (( have + 1 >= NC_MAJOR )) || target=$(( have + 1 ))
    echo "Installed Nextcloud ${have}, image nextcloud:${target}"
  fi

  local host generated=""
  if [[ -n "$NC_DOMAIN" ]]; then
    host="$NC_DOMAIN"; URL="https://${NC_DOMAIN}"
  else
    host="$(public_ip)"; URL="http://${host}"
    [[ "$NC_PORT" == 80 ]] || URL+=":${NC_PORT}"
  fi
  TRUSTED="localhost ${host}"

  INSTALL_PASSWORD=""
  if ! is_installed && [[ -e "$DIR/html/version.php" ]]; then
    # the image installs only into an empty folder: an attempt that broke off would never be retried
    warn "An earlier installation did not finish (no users, no files yet): starting over"
    compose_down "$DIR"
    rm -rf "$DIR/html" "$DIR/db"
  fi
  if ! is_installed; then
    INSTALL_PASSWORD="${new_password:-$(rand_pass 20)}"
    [[ -n "$new_password" ]] || generated="$INSTALL_PASSWORD"
  fi

  compose_down "$DIR"
  if [[ -n "$NC_DOMAIN" ]]; then
    check_dns "$NC_DOMAIN"
    require_free_port 80 tcp
    require_free_port 443 tcp
  else
    require_free_port "$NC_PORT" tcp
  fi

  write_files "nextcloud:${target}-apache"
  log "Starting Nextcloud"
  compose_pull "$DIR"
  (cd "$DIR" && docker compose up -d)
  wait_ready

  log "Configuring"
  occ config:system:set trusted_domains 0 --value=localhost >/dev/null
  occ config:system:set trusted_domains 1 --value="$host" >/dev/null
  occ config:system:set overwrite.cli.url --value="$URL" >/dev/null
  occ config:system:set maintenance_window_start --type=integer --value=1 >/dev/null
  occ background:cron >/dev/null
  occ db:add-missing-indices >/dev/null 2>&1 || true
  if [[ -n "$new_password" && -z "$INSTALL_PASSWORD" ]]; then
    echo "Setting a new password for ${NC_ADMIN_USER}"
    (cd "$DIR" && docker compose exec -T -u www-data -e OC_PASS="$new_password" app php occ user:resetpassword --password-from-env "$NC_ADMIN_USER") ||
      die "Could not set the password (does the user ${NC_ADMIN_USER} exist?)"
  fi
  if [[ -n "$INSTALL_PASSWORD" ]]; then
    # the admin password is not needed on disk (or in the container) after the installation
    INSTALL_PASSWORD=""
    write_files "nextcloud:${target}-apache"
    (cd "$DIR" && docker compose up -d)
    wait_ready >/dev/null
  fi

  if [[ -n "$NC_DOMAIN" ]]; then open_port 80 tcp; open_port 443 tcp; open_port 443 udp; else open_port "$NC_PORT" tcp; fi

  echo ""
  echo "Nextcloud: ${URL}"
  echo "Admin: ${NC_ADMIN_USER}${generated:+ / password ${generated}}"
  [[ "$target" == "$NC_MAJOR" ]] || echo "Upgraded to ${target}; run the script again to continue to ${NC_MAJOR}."
  echo "Files and config: $DIR/html (user data in $DIR/html/data), database: $DIR/db"
  echo "occ: cd $DIR && docker compose exec -u www-data app php occ status"

  sshm_result NC_URL "$URL"
  sshm_result NC_ADMIN_USER "$NC_ADMIN_USER"
  [[ -z "$generated" ]] || sshm_result NC_ADMIN_PASSWORD "$generated"
  if [[ -n "$NC_DOMAIN" ]]; then sshm_result NC_PORT 443; else sshm_result NC_PORT "$NC_PORT"; fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
