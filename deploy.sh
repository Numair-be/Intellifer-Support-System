#!/usr/bin/env bash
# Deploys Intellifer Hub with Docker Compose on a Linux server.
#
#   ./deploy.sh                  build and start the hub (API, worker, database)
#   ./deploy.sh --with-whatsapp  also start OpenWA (sends a pairing request if WhatsApp is not linked yet)
#   ./deploy.sh --check          only check prerequisites and pick free ports; change nothing in Docker
#
# Safe to run again for updates: secrets, the database password and ports already in .env are kept,
# and ports held by this hub's own containers count as free.
set -euo pipefail

cd "$(dirname "$0")"
PROJECT="intellifer"
WITH_WHATSAPP=0
CHECK_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --with-whatsapp) WITH_WHATSAPP=1 ;;
    --check) CHECK_ONLY=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

say()  { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33mwarning:\033[0m %s\n' "$*" >&2; }
fail() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- prerequisites
say "Checking prerequisites"
command -v docker >/dev/null 2>&1 || fail "Docker is not installed. See https://docs.docker.com/engine/install/"
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is missing ('docker compose'). Install the docker-compose-plugin package."
docker info >/dev/null 2>&1 || fail "Cannot talk to Docker. Is it running, and is this user in the 'docker' group (or use sudo)?"
command -v ss >/dev/null 2>&1 || command -v netstat >/dev/null 2>&1 \
  || fail "Need 'ss' (iproute2) or 'netstat' (net-tools) to see which ports are in use."
[ -f docker-compose.yml ] || fail "Run this from the project folder (docker-compose.yml not found)."

# ---------------------------------------------------------------- .env helpers
if [ ! -f .env ]; then
  [ -f .env.example ] || fail ".env and .env.example are both missing."
  cp .env.example .env
  chmod 600 .env
  say "Created .env from .env.example"
fi

# Windows line endings would leave a hidden carriage return on every value.
sed -i 's/\r$//' .env

get_env() { grep -E "^$1=" .env | tail -n 1 | cut -d= -f2- | tr -d '\r' || true; }
set_env() {
  local key="$1" value="$2" tmp
  tmp="$(mktemp)"
  if grep -qE "^$key=" .env; then
    awk -v k="$key" -v v="$value" 'BEGIN{FS=OFS="="} $1==k {print k "=" v; next} {print}' .env > "$tmp"
  else
    cat .env > "$tmp"
    printf '%s=%s\n' "$key" "$value" >> "$tmp"
  fi
  cat "$tmp" > .env
  rm -f "$tmp"
}
random_text() { head -c 64 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "${1:-40}"; }

# ---------------------------------------------------------------- secrets
say "Checking secrets"
for key in HUB_API_KEY OPENWA_WEBHOOK_SECRET WA_API_KEY; do
  value="$(get_env "$key")"
  # The WhatsApp gateway refuses to start in production with a key shorter than 32 characters.
  if [ -z "$value" ] || [[ "$value" == dev-only-* ]] || [ "${#value}" -lt 32 ]; then
    set_env "$key" "$(random_text 40)"
    echo "  generated $key"
  fi
done


# The database password only takes effect when the database is first created. Changing it on an existing
# database would lock the hub out, so a new one is only generated for a fresh install.
db_volume_exists=0
docker volume inspect "${PROJECT}_hub-db-data" >/dev/null 2>&1 && db_volume_exists=1
db_password="$(get_env POSTGRES_PASSWORD)"
if [ "$db_volume_exists" = 0 ] && { [ -z "$db_password" ] || [ "$db_password" = "intellifer" ]; }; then
  set_env POSTGRES_PASSWORD "$(random_text 32)"
  echo "  generated POSTGRES_PASSWORD (fresh database)"
elif [ "$db_password" = "intellifer" ]; then
  warn "POSTGRES_PASSWORD is still the default, but the database already exists, so it is left as is."
fi

# ---------------------------------------------------------------- ports
say "Checking ports"

# Every TCP port something on this server is listening on, on any address.
listening_ports() {
  if command -v ss >/dev/null 2>&1; then
    ss -Hltn 2>/dev/null | awk '{print $4}'
  else
    netstat -ltn 2>/dev/null | awk 'NR>2 {print $4}'
  fi | sed -E 's/.*[:.]([0-9]+)$/\1/' | grep -E '^[0-9]+$' | sort -un
}

# Host ports published by Docker containers, split into this hub's own and everyone else's.
# (Docker can publish ports without a listening socket that ss would see.)
docker_ports() {
  docker ps --format '{{.Label "com.docker.compose.project"}}|{{.Ports}}' 2>/dev/null \
    | awk -F'|' -v want="$1" -v project="$PROJECT" '(want=="ours") == ($1==project) {print $2}' \
    | grep -oE ':[0-9]+->' | tr -d ':>-' | sort -un
}

LISTENING="$(listening_ports)"
OURS="$(docker_ports ours || true)"
OTHERS="$(docker_ports others || true)"
TAKEN_BY_HUB=" "

port_busy() {
  local port="$1"
  # A port this hub's own running container holds is fine: it is ours, and it moves when we restart.
  if grep -qx "$port" <<<"$OURS"; then return 1; fi
  grep -qx "$port" <<<"$LISTENING" && return 0
  grep -qx "$port" <<<"$OTHERS" && return 0
  [[ "$TAKEN_BY_HUB" == *" $port "* ]] && return 0
  return 1
}

choose_port() {
  local key="$1" default="$2" port start
  port="$(get_env "$key")"
  [[ "$port" =~ ^[0-9]+$ ]] || port="$default"
  start="$port"
  while port_busy "$port"; do
    port=$((port + 1))
    [ "$port" -le 65535 ] || fail "No free port found for $key above $start."
  done
  if [ "$port" != "$start" ]; then
    echo "  $key: $start is in use, using $port"
  else
    echo "  $key: $port"
  fi
  set_env "$key" "$port"
  TAKEN_BY_HUB="$TAKEN_BY_HUB$port "
}

choose_port HUB_API_PORT 8080
choose_port HUB_DB_PORT 5432
choose_port OPENWA_PORT 8081
[ -n "$(get_env HUB_API_BIND)" ] || set_env HUB_API_BIND 127.0.0.1

if [ "$CHECK_ONLY" = 1 ]; then
  say "Check finished. Ports and secrets are in .env; nothing was started."
  exit 0
fi

# ---------------------------------------------------------------- build and start
compose() { docker compose -p "$PROJECT" "$@"; }

say "Building images (retries on network errors)"
built=0
for attempt in 1 2 3; do
  if compose build hub-api hub-worker; then built=1; break; fi
  warn "Build attempt $attempt failed; retrying in 15 seconds (package downloads are sometimes flaky)."
  sleep 15
done
[ "$built" = 1 ] || fail "The build failed three times. Check the output above and the server's internet access."

say "Starting the hub"
compose up -d hub-db hub-api hub-worker

if [ "$WITH_WHATSAPP" = 1 ]; then
  say "Starting OpenWA"
  [ -n "$(get_env WA_LINK_PHONE)" ] && warn "WA_LINK_PHONE is set: if WhatsApp is not linked yet, that phone gets a pairing request now."
  compose --profile openwa up -d openwa
fi

say "Waiting for the API to become healthy"
api_container="$(compose ps -q hub-api)"
for _ in $(seq 1 60); do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$api_container" 2>/dev/null || echo starting)"
  [ "$status" = healthy ] && break
  sleep 3
done
[ "$status" = healthy ] || fail "hub-api is '$status'. See: docker compose -p $PROJECT logs hub-api"

api_port="$(get_env HUB_API_PORT)"
cat <<EOF

Intellifer Hub is running.
  API          http://$(get_env HUB_API_BIND):$api_port   (health: /health/ready)
  Database     127.0.0.1:$(get_env HUB_DB_PORT)
  OpenWA       127.0.0.1:$(get_env OPENWA_PORT)   $( [ "$WITH_WHATSAPP" = 1 ] && echo "(started)" || echo "(not started: ./deploy.sh --with-whatsapp)" )
  API key      in .env as HUB_API_KEY (send it as the X-Api-Key header)

Logs:          docker compose -p $PROJECT logs -f hub-worker
Plane links:   curl -H "X-Api-Key: \$(grep ^HUB_API_KEY= .env | cut -d= -f2-)" http://127.0.0.1:$api_port/api/plane/check
EOF
