#!/usr/bin/env bash
# @name Docker Engine + Docker Compose
# @name_en Docker Engine + Docker Compose
# @description Ставит Docker официальным скриптом get.docker.com (Debian, Ubuntu, CentOS, RHEL, Fedora…) и включает автозапуск.
# @description Если Docker уже установлен, только проверяет его и показывает версии.
# @description_en Installs Docker with the official get.docker.com script (Debian, Ubuntu, CentOS, RHEL, Fedora…) and enables it on boot.
# @description_en If Docker is already there, it only checks it and shows the versions.
#
# @param DOCKER_MIRROR choice options=official,Aliyun,AzureChinaCloud default=official label="Откуда качать пакеты" label_en="Package mirror" hint="official — download.docker.com; зеркала — если он недоступен из региона сервера" hint_en="official = download.docker.com; mirrors help when it is blocked in the server's region"
#
# @result DOCKER_VERSION label="Версия Docker" label_en="Docker version"
# @result COMPOSE_VERSION label="Версия Docker Compose" label_en="Docker Compose version"
set -Eeuo pipefail

log(){ echo -e "\n== $* =="; }
die(){ echo "ERROR: $*" >&2; exit 1; }
cmd(){ command -v "$1" >/dev/null 2>&1; }
# SSH Manager: report a value back (saved to the server's attributes); no-op when run by hand
sshm_result(){ [[ -n "${SSHM_RESULT:-}" ]] && printf '%s=%s\n' "$1" "$2" >> "$SSHM_RESULT"; return 0; }

[[ "${EUID}" -eq 0 ]] || die "Run as root: sudo bash $0"

fetch(){ # url file
  if cmd curl; then curl -fsSL "$1" -o "$2"
  elif cmd wget; then wget -qO "$2" "$1"
  else
    log "Installing curl"
    if cmd apt-get; then apt-get update -y && apt-get install -y curl ca-certificates
    elif cmd dnf; then dnf install -y curl
    elif cmd yum; then yum install -y curl
    elif cmd apk; then apk add --no-cache curl
    else die "Neither curl nor wget found"
    fi
    curl -fsSL "$1" -o "$2"
  fi
}

if cmd docker && docker compose version >/dev/null 2>&1; then
  log "Docker is already installed"
else
  log "Installing Docker Engine + compose plugin (get.docker.com)"
  tmp="$(mktemp)"
  trap 'rm -f "$tmp"' EXIT
  fetch https://get.docker.com "$tmp"
  args=()
  [[ "${DOCKER_MIRROR:-official}" == "official" ]] || args=(--mirror "${DOCKER_MIRROR}")
  sh "$tmp" ${args[@]+"${args[@]}"}
fi

if cmd systemctl; then systemctl enable --now docker >/dev/null 2>&1 || true
elif cmd service; then service docker start >/dev/null 2>&1 || true
fi

log "Checking"
docker version --format 'Docker {{.Server.Version}} ({{.Server.Os}}/{{.Server.Arch}})' || die "docker does not answer"
docker compose version || die "docker compose plugin is missing"
docker run --rm hello-world >/dev/null 2>&1 && echo "hello-world: OK" || echo "WARNING: could not run hello-world (no internet access to Docker Hub?)"

sshm_result DOCKER_VERSION "$(docker version --format '{{.Server.Version}}')"
sshm_result COMPOSE_VERSION "$(docker compose version --short 2>/dev/null || true)"
