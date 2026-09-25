#!/usr/bin/env bash
# @name Статический сайт (Caddy в Docker) — Ubuntu / Debian / CentOS
# @name_en Static website (Caddy in Docker) — Ubuntu / Debian / CentOS
# @group Web
# @os ubuntu,debian,centos,rhel
# @description Ставит Docker и веб-сервер Caddy, который раздаёт папку /opt/static-site/site со страницей-заглушкой.
# @description С доменом HTTPS включается сам: Caddy получает и продлевает сертификат Let's Encrypt (нужны порты 80 и 443).
# @description Без домена сайт открывается по IP на выбранном порту. Свои файлы загрузите в /opt/static-site/site через «Файлы».
# @description_en Installs Docker and the Caddy web server serving /opt/static-site/site with a starter page.
# @description_en With a domain HTTPS just works: Caddy gets and renews a Let's Encrypt certificate (ports 80 and 443 are needed).
# @description_en Without a domain the site opens by IP on the chosen port. Upload your files to /opt/static-site/site with "Files".
#
# @param SITE_DOMAIN text label="Домен сайта" label_en="Site domain" hint="С доменом — HTTPS на портах 80/443; пусто — HTTP по IP" hint_en="With a domain: HTTPS on ports 80/443; empty: HTTP by IP"
# @param SITE_PORT number default=8080 label="HTTP-порт (без домена)" label_en="HTTP port (no domain)" hint="Используется, только если домен не задан" hint_en="Used only when there is no domain"
# @param ACME_EMAIL text label="E-mail для Let's Encrypt" label_en="E-mail for Let's Encrypt" hint="Необязательно, только с доменом" hint_en="Optional, only with a domain"
# @param SITE_TITLE text default="It works!" label="Заголовок страницы-заглушки" label_en="Starter page title"
# @param SITE_OVERWRITE bool default=0 label="Перезаписать index.html" label_en="Overwrite index.html"
#
# @result SITE_URL label="Адрес сайта" label_en="Site URL"
# @result SITE_ROOT label="Папка сайта" label_en="Site folder"
# @result SITE_PORT label="Порт сайта" label_en="Site port" monitor=Caddy
#
# Runs standalone too: sudo SITE_PORT=8080 bash static-site-docker.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

DIR=/opt/static-site
CONTAINER=static-site

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

# ---- shared helpers: site (identical in every built-in script that has them; edit all copies) ----
html_escape(){
  local s="$1"
  s="${s//&/&amp;}"; s="${s//</&lt;}"; s="${s//>/&gt;}"; s="${s//\"/&quot;}"
  printf '%s' "$s"
}

# A starter page, only when there is none yet (or SITE_OVERWRITE=1)
write_index(){ # folder
  if [[ -f "$1/index.html" && "${SITE_OVERWRITE:-0}" != 1 ]]; then
    echo "Keeping the existing $1/index.html"
    return 0
  fi
  local title; title="$(html_escape "$SITE_TITLE")"
  cat > "$1/index.html" <<HTML
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${title}</title>
<style>
  :root { color-scheme: light dark; --bg: #f6f7f9; --fg: #1d2330; --muted: #5b6475; --card: #fff; }
  @media (prefers-color-scheme: dark) { :root { --bg: #14171c; --fg: #e8ebf0; --muted: #9aa3b2; --card: #1d2128; } }
  body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--fg);
         font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
  main { background: var(--card); padding: 40px 48px; border-radius: 16px; box-shadow: 0 8px 30px rgba(0,0,0,.08); max-width: 560px; margin: 16px; }
  h1 { margin: 0 0 8px; font-size: 28px; }
  p { margin: 0; color: var(--muted); }
</style>
</head>
<body>
<main>
  <h1>${title}</h1>
  <p>The site works. Replace this page: upload your files to ${1}.</p>
</main>
</body>
</html>
HTML
  chmod 644 "$1/index.html"
  echo "Wrote $1/index.html"
}
# ---- end of shared helpers: site ----

write_files(){
  log "Writing $DIR"
  {
    if [[ -n "$SITE_DOMAIN" && -n "$ACME_EMAIL" ]]; then printf '{\n\temail %s\n}\n\n' "$ACME_EMAIL"; fi
    if [[ -n "$SITE_DOMAIN" ]]; then echo "${SITE_DOMAIN} {"; else echo ":80 {"; fi
    printf '\troot * /srv\n\tencode zstd gzip\n\tfile_server\n}\n'
  } > "$DIR/Caddyfile"
  local ports
  if [[ -n "$SITE_DOMAIN" ]]; then
    ports=$'      - "80:80"\n      - "443:443"\n      - "443:443/udp"'
  else
    ports="      - \"${SITE_PORT}:80\""
  fi
  cat > "$DIR/docker-compose.yml" <<YAML
services:
  caddy:
    image: caddy:2-alpine
    container_name: ${CONTAINER}
    restart: unless-stopped
    ports:
${ports}
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - ./site:/srv:ro
      - ./caddy/data:/data
      - ./caddy/config:/config
    logging:
      driver: local
      options:
        max-size: "2m"
        max-file: "3"
YAML
}

main(){
  require_root
  require_os

  SITE_DOMAIN="$(trim "${SITE_DOMAIN:-}")"; SITE_DOMAIN="${SITE_DOMAIN,,}"
  SITE_PORT="$(trim "${SITE_PORT:-8080}")"
  ACME_EMAIL="$(trim "${ACME_EMAIL:-}")"
  SITE_TITLE="$(trim "${SITE_TITLE:-It works!}")"

  [[ -z "$SITE_DOMAIN" ]] || valid_domain "$SITE_DOMAIN" || die "Invalid domain: $SITE_DOMAIN"
  if [[ -z "$SITE_DOMAIN" ]]; then
    [[ "$SITE_PORT" =~ ^[0-9]+$ ]] && (( SITE_PORT >= 1 && SITE_PORT <= 65535 )) || die "Invalid port: $SITE_PORT"
  fi

  pkg_install ca-certificates curl openssl iproute2
  ensure_docker
  install -d -m 0755 "$DIR" "$DIR/site" "$DIR/caddy"

  compose_down "$DIR"
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  if [[ -n "$SITE_DOMAIN" ]]; then
    check_dns "$SITE_DOMAIN"
    require_free_port 80 tcp
    require_free_port 443 tcp
  else
    require_free_port "$SITE_PORT" tcp
  fi

  write_index "$DIR/site"
  write_files
  log "Starting Caddy"
  compose_pull "$DIR"
  (cd "$DIR" && docker compose up -d)

  local url port
  if [[ -n "$SITE_DOMAIN" ]]; then
    open_port 80 tcp; open_port 443 tcp; open_port 443 udp
    url="https://${SITE_DOMAIN}"; port=443
    wait_http "http://127.0.0.1/" 30 || { docker logs --tail 50 "$CONTAINER" || true; die "Caddy does not answer"; }
    echo "Caddy gets the certificate in the background; the first HTTPS request may take a few seconds."
  else
    open_port "$SITE_PORT" tcp
    url="http://$(public_ip)"; port="$SITE_PORT"
    [[ "$SITE_PORT" == 80 ]] || url+=":${SITE_PORT}"
    wait_http "http://127.0.0.1:${SITE_PORT}/" 30 || { docker logs --tail 50 "$CONTAINER" || true; die "Caddy does not answer"; }
  fi

  echo ""
  echo "Site: ${url}"
  echo "Files: $DIR/site (upload yours there, the starter page is index.html)"
  echo "Logs: docker logs --tail 100 -f ${CONTAINER}"

  sshm_result SITE_URL "$url"
  sshm_result SITE_ROOT "$DIR/site"
  sshm_result SITE_PORT "$port"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
