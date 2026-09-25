#!/usr/bin/env bash
# @name File Browser — лёгкое хранилище (Docker) — Ubuntu / Debian / CentOS
# @name_en File Browser — lightweight storage (Docker) — Ubuntu / Debian / CentOS
# @group Cloud
# @os ubuntu,debian,centos,rhel
# @description File Browser: веб-интерфейс к папке на сервере — загрузка, скачивание, общие ссылки, пользователи. Без базы данных,
# @description хватает 256 МБ памяти. С доменом — HTTPS через Caddy (порты 80 и 443), без домена — HTTP по IP на выбранном порту.
# @description Повторный запуск обновляет образ; новый пароль администратора задаётся, если его ввести.
# @description_en File Browser: a web interface to a folder on the server — upload, download, share links, users. No database,
# @description_en 256 MB of memory is enough. With a domain: HTTPS through Caddy (ports 80 and 443); without one: HTTP by IP on the chosen port.
# @description_en Running it again updates the image; a new admin password is set when one is entered.
#
# @param FB_DOMAIN text label="Домен" label_en="Domain" hint="С доменом — HTTPS на портах 80/443; пусто — HTTP по IP" hint_en="With a domain: HTTPS on ports 80/443; empty: HTTP by IP"
# @param FB_PORT number default=8081 label="HTTP-порт (без домена)" label_en="HTTP port (no domain)" hint="Используется, только если домен не задан" hint_en="Used only when there is no domain"
# @param ACME_EMAIL text label="E-mail для Let's Encrypt" label_en="E-mail for Let's Encrypt" hint="Необязательно, только с доменом" hint_en="Optional, only with a domain"
# @param FB_ROOT text required default=/opt/filebrowser/files label="Папка с файлами" label_en="Files folder" hint="Что будет видно в веб-интерфейсе" hint_en="What the web interface shows"
# @param FB_ADMIN_USER text required default=admin label="Администратор" label_en="Admin user"
# @param FB_ADMIN_PASSWORD secret label="Пароль администратора" label_en="Admin password" hint="Не короче 12 символов. Пусто — сгенерировать при установке; при повторном запуске — задать новый" hint_en="At least 12 characters. Empty = generate on install; on a later run: set a new one"
#
# @result FB_URL label="Адрес File Browser" label_en="File Browser URL"
# @result FB_ADMIN_USER label="Администратор File Browser" label_en="File Browser admin"
# @result FB_ADMIN_PASSWORD label="Пароль администратора File Browser" label_en="File Browser admin password"
# @result FB_PORT label="Порт File Browser" label_en="File Browser port" monitor="File Browser"
#
# Runs standalone too: sudo FB_PORT=8081 bash filebrowser.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

DIR=/opt/filebrowser
IMAGE=filebrowser/filebrowser:v2

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

# filebrowser CLI against the database (the server must be stopped: the database is locked while it runs)
fb(){ (cd "$DIR" && docker compose run --rm --no-deps -T filebrowser "$@"); }

write_files(){
  log "Writing $DIR"
  local ports="" caddy=""
  if [[ -n "$FB_DOMAIN" ]]; then
    caddy="$(caddy_service)"
    write_caddyfile "$FB_DOMAIN" "filebrowser:80"
  else
    ports=$'    ports:\n      - "'"${FB_PORT}"$':80"'
  fi
  cat > "$DIR/docker-compose.yml" <<YAML
services:
  filebrowser:
    image: ${IMAGE}
    restart: unless-stopped
    user: "${RUN_AS}"
${ports}
    volumes:
      - ${FB_ROOT}:/srv
      - ./database:/database
      - ./config:/config
    logging:
      driver: local
      options:
        max-size: "2m"
        max-file: "3"
${caddy}
YAML
}

main(){
  require_root
  require_os

  FB_DOMAIN="$(trim "${FB_DOMAIN:-}")"; FB_DOMAIN="${FB_DOMAIN,,}"
  FB_PORT="$(trim "${FB_PORT:-8081}")"
  ACME_EMAIL="$(trim "${ACME_EMAIL:-}")"
  FB_ROOT="$(trim "${FB_ROOT:-/opt/filebrowser/files}")"; FB_ROOT="${FB_ROOT%/}"
  FB_ADMIN_USER="$(trim "${FB_ADMIN_USER:-admin}")"
  local new_password="${FB_ADMIN_PASSWORD:-}"

  [[ -z "$FB_DOMAIN" ]] || valid_domain "$FB_DOMAIN" || die "Invalid domain: $FB_DOMAIN"
  [[ -n "$FB_DOMAIN" ]] || { [[ "$FB_PORT" =~ ^[0-9]+$ ]] && (( FB_PORT >= 1 && FB_PORT <= 65535 )); } || die "Invalid port: $FB_PORT"
  [[ "$FB_ROOT" == /* && "$FB_ROOT" =~ ^[A-Za-z0-9._/-]+$ ]] || die "The files folder must be an absolute path of letters, digits and . _ - /"
  [[ "$FB_ROOT" != / && "$FB_ROOT" != /etc* && "$FB_ROOT" != /proc* && "$FB_ROOT" != /sys* ]] || die "Do not share ${FB_ROOT:-/}"
  [[ "$FB_ADMIN_USER" =~ ^[A-Za-z0-9._@-]+$ ]] || die "The admin name may contain letters, digits and . _ @ - only"
  [[ -z "$new_password" || ${#new_password} -ge 12 ]] || die "The password must be at least 12 characters"
  [[ "$new_password" != *[$'\n\r']* ]] || die "Invalid password"

  pkg_install ca-certificates curl openssl iproute2
  check_resources 256 500
  ensure_docker
  install -d -m 0755 "$DIR"

  # the image runs as uid 1000; an existing folder keeps its owner and the server runs as that owner
  if [[ -d "$FB_ROOT" ]]; then
    RUN_AS="$(stat -c '%u:%g' "$FB_ROOT")"
  else
    install -d -m 0755 -o 1000 -g 1000 "$FB_ROOT"
    RUN_AS="1000:1000"
  fi
  install -d -m 0700 "$DIR/database" "$DIR/config"
  chown -R "$RUN_AS" "$DIR/database" "$DIR/config"
  echo "Serving ${FB_ROOT} as uid:gid ${RUN_AS}"

  compose_down "$DIR"
  if [[ -n "$FB_DOMAIN" ]]; then
    check_dns "$FB_DOMAIN"
    require_free_port 80 tcp
    require_free_port 443 tcp
  else
    require_free_port "$FB_PORT" tcp
  fi

  write_files
  compose_pull "$DIR"
  if [[ -f "$DIR/config/settings.json" ]]; then :; else
    printf '{\n  "port": 80,\n  "baseURL": "",\n  "address": "",\n  "log": "stdout",\n  "database": "/database/filebrowser.db",\n  "root": "/srv"\n}\n' > "$DIR/config/settings.json"
    chown "$RUN_AS" "$DIR/config/settings.json"
  fi

  local generated=""
  if [[ ! -f "$DIR/database/filebrowser.db" ]]; then
    log "Creating the database and the admin user"
    local password="${new_password:-$(rand_pass 20)}"
    [[ -n "$new_password" ]] || generated="$password"
    fb config init >/dev/null
    fb users add "$FB_ADMIN_USER" "$password" --perm.admin >/dev/null
  elif [[ -n "$new_password" ]]; then
    echo "Setting a new password for ${FB_ADMIN_USER}"
    fb users update "$FB_ADMIN_USER" --password "$new_password" >/dev/null ||
      fb users add "$FB_ADMIN_USER" "$new_password" --perm.admin >/dev/null
  fi

  log "Starting File Browser"
  (cd "$DIR" && docker compose up -d)
  local url port local_url
  if [[ -n "$FB_DOMAIN" ]]; then
    url="https://${FB_DOMAIN}"; port=443; local_url="http://127.0.0.1/"
    open_port 80 tcp; open_port 443 tcp; open_port 443 udp
  else
    url="http://$(public_ip)"; port="$FB_PORT"; local_url="http://127.0.0.1:${FB_PORT}/"
    [[ "$FB_PORT" == 80 ]] || url+=":${FB_PORT}"
    open_port "$FB_PORT" tcp
  fi
  wait_http "$local_url" 60 || { (cd "$DIR" && docker compose logs --tail 50) || true; die "File Browser does not answer"; }

  echo ""
  echo "File Browser: ${url}"
  echo "Admin: ${FB_ADMIN_USER}${generated:+ / password ${generated}}"
  echo "Files: ${FB_ROOT}; database: $DIR/database"

  sshm_result FB_URL "$url"
  sshm_result FB_ADMIN_USER "$FB_ADMIN_USER"
  [[ -z "$generated" ]] || sshm_result FB_ADMIN_PASSWORD "$generated"
  sshm_result FB_PORT "$port"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
