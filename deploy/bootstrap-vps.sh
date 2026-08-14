#!/usr/bin/env bash
# Bootstrap CSX on Ubuntu 24 after DNS for cscstocks.com points at this VPS.
#
#   curl -fsSL https://raw.githubusercontent.com/ethsmith/csc-stock-api/main/deploy/bootstrap-vps.sh | sudo bash
#
# Or copy the file to the VPS and: sudo bash bootstrap-vps.sh
#
# Optional env:
#   DOMAIN              default cscstocks.com
#   CERTBOT_EMAIL       Let's Encrypt account email (prompted if unset)
#   DISCORD_CLIENT_SECRET
#   DISCORD_ADMIN_ID    your Discord user snowflake (admin on first login)
#   DISCORD_CLIENT_ID   default 1537811545894035517
#   POSTGRES_PASSWORD   generated if unset
#   GH_TOKEN            if the GitHub repos are private
#   SKIP_CERTBOT=1      HTTP-only (not for real traffic)
#   CSX_BRANCH          default main
set -euo pipefail

DOMAIN="${DOMAIN:-cscstocks.com}"
BRANCH="${CSX_BRANCH:-main}"
DISCORD_CLIENT_ID="${DISCORD_CLIENT_ID:-1537811545894035517}"
API_REPO="${API_REPO:-https://github.com/ethsmith/csc-stock-api.git}"
FRONTEND_REPO="${FRONTEND_REPO:-https://github.com/ethsmith/csc-stock-frontend.git}"
API_SRC=/opt/csx/src/api
FRONTEND_SRC=/opt/csx/src/frontend

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root: sudo bash $0" >&2
  exit 1
fi

prompt() {
  local var="$1" msg="$2" silent="${3:-0}"
  if [[ -n "${!var:-}" ]]; then
    return
  fi
  local tty=/dev/tty
  if [[ ! -r "$tty" ]]; then
    echo "Set $var in the environment (stdin is not a terminal)." >&2
    exit 1
  fi
  if [[ "$silent" == 1 ]]; then
    read -r -s -p "$msg" "$var" < "$tty"
    echo
  else
    read -r -p "$msg" "$var" < "$tty"
  fi
}

clone_repo() {
  local url="$1" dest="$2"
  export GIT_TERMINAL_PROMPT=0
  echo "Cloning $url -> $dest"
  if [[ -d "$dest/.git" ]]; then
    git -C "$dest" fetch origin
    git -C "$dest" checkout "$BRANCH"
    git -C "$dest" reset --hard "origin/$BRANCH"
    return
  fi
  mkdir -p "$(dirname "$dest")"
  rm -rf "$dest"
  if [[ -n "${GH_TOKEN:-}" ]]; then
    local authed="${url/https:\/\//https:\/\/x-access-token:${GH_TOKEN}@}"
    git clone --depth 1 --branch "$BRANCH" "$authed" "$dest"
  else
    git clone --depth 1 --branch "$BRANCH" "$url" "$dest"
  fi
}

public_ip() {
  curl -4 -fsS --max-time 8 https://ifconfig.me/ip || true
}

dns_ip() {
  getent ahostsv4 "$1" 2>/dev/null | awk '{ print $1; exit }'
}

echo "==> CSX VPS bootstrap for https://${DOMAIN}"

prompt CERTBOT_EMAIL "Let's Encrypt email: "
prompt DISCORD_CLIENT_SECRET "Discord OAuth client secret: " 1
prompt DISCORD_ADMIN_ID "Your Discord user id (admin): "

if [[ -z "$CERTBOT_EMAIL" || -z "$DISCORD_CLIENT_SECRET" || -z "$DISCORD_ADMIN_ID" ]]; then
  echo "CERTBOT_EMAIL, DISCORD_CLIENT_SECRET, and DISCORD_ADMIN_ID are required." >&2
  exit 1
fi

echo "==> Installing packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get upgrade -y
apt-get install -y \
  ca-certificates curl gnupg git unzip ufw nginx certbot python3-certbot-nginx \
  debian-keyring debian-archive-keyring apt-transport-https

if ! command -v docker >/dev/null 2>&1; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
    >/etc/apt/sources.list.d/docker.list
  apt-get update -y
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
fi
systemctl enable --now docker

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb
  dpkg -i /tmp/packages-microsoft-prod.deb
  rm -f /tmp/packages-microsoft-prod.deb
  apt-get update -y
  apt-get install -y aspnetcore-runtime-10.0 dotnet-sdk-10.0
fi

if ! command -v node >/dev/null 2>&1; then
  curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
  apt-get install -y nodejs
fi

echo "==> Firewall"
ufw allow OpenSSH
ufw allow 'Nginx Full'
ufw --force enable

echo "==> Directories"
mkdir -p /opt/csx/db /opt/csx/src /var/www/csx/api /var/www/csx/frontend /var/cache/csx /etc/csx
chmod 755 /etc/csx

echo "==> Postgres"
if [[ ! -f /etc/csx/db.env ]]; then
  POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-$(openssl rand -base64 18 | tr -d '/+=' | head -c 24)}"
  umask 077
  cat >/etc/csx/db.env <<EOF
POSTGRES_USER=csx
POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
POSTGRES_DB=csx
EOF
fi
umask 022
chmod 600 /etc/csx/db.env
# shellcheck disable=SC1091
source /etc/csx/db.env

cat >/opt/csx/db/docker-compose.yml <<'EOF'
services:
  db:
    image: postgres:16-alpine
    restart: unless-stopped
    env_file:
      - /etc/csx/db.env
    ports:
      - "127.0.0.1:5432:5432"
    volumes:
      - csx-pg:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U csx -d csx"]
      interval: 5s
      timeout: 5s
      retries: 20
volumes:
  csx-pg:
EOF
docker compose -f /opt/csx/db/docker-compose.yml up -d
echo "Waiting for Postgres..."
for _ in $(seq 1 40); do
  if docker compose -f /opt/csx/db/docker-compose.yml exec -T db pg_isready -U csx -d csx >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

echo "==> Clone GitHub repos"
clone_repo "$API_REPO" "$API_SRC"
clone_repo "$FRONTEND_REPO" "$FRONTEND_SRC"
chmod +x \
  "$API_SRC/deploy/update.sh" \
  "$API_SRC/deploy/csx-deploy.sh" \
  "$API_SRC/deploy/bootstrap-vps.sh" \
  "$FRONTEND_SRC/deploy/update.sh"

echo "==> App secrets"
# shellcheck disable=SC1091
source /etc/csx/db.env
if [[ ! -f /etc/csx/csx.env ]]; then
  JWT_KEY="$(openssl rand -base64 48)"
  umask 077
  cat >/etc/csx/csx.env <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5233
ConnectionStrings__Csx=Host=127.0.0.1;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
Jwt__SigningKey=${JWT_KEY}
Discord__ClientId=${DISCORD_CLIENT_ID}
Discord__ClientSecret=${DISCORD_CLIENT_SECRET}
Discord__RedirectUri=https://${DOMAIN}/api/v1/auth/discord/callback
Discord__AdminDiscordIds__0=${DISCORD_ADMIN_ID}
Frontend__Origin=https://${DOMAIN}
Cors__Origins__0=https://${DOMAIN}
EOF
else
  echo "Keeping existing /etc/csx/csx.env"
fi
chmod 600 /etc/csx/csx.env

cat >/etc/csx/frontend.env <<EOF
VITE_API_URL=https://${DOMAIN}
EOF
chmod 644 /etc/csx/frontend.env

echo "==> systemd units"
cp "$API_SRC/deploy/csx-api.service" /etc/systemd/system/
cp "$API_SRC/deploy/csx-deploy.service" /etc/systemd/system/
cp "$FRONTEND_SRC/deploy/csx-frontend.service" /etc/systemd/system/
systemctl daemon-reload

write_nginx_http() {
  cat >/etc/nginx/sites-available/cscstocks <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name ${DOMAIN} www.${DOMAIN};

    location / {
        return 200 'csx-setup';
        add_header Content-Type text/plain;
    }
}
EOF
  ln -sfn /etc/nginx/sites-available/cscstocks /etc/nginx/sites-enabled/cscstocks
  rm -f /etc/nginx/sites-enabled/default
  nginx -t
  systemctl enable --now nginx
  systemctl reload nginx
}

write_nginx_ssl() {
  local www_block=""
  if [[ -n "$(dns_ip "www.${DOMAIN}")" ]]; then
    www_block=$(cat <<EOF

server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name www.${DOMAIN};
    ssl_certificate     /etc/letsencrypt/live/${DOMAIN}/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/${DOMAIN}/privkey.pem;
    include /etc/letsencrypt/options-ssl-nginx.conf;
    ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem;
    return 301 https://${DOMAIN}\$request_uri;
}
EOF
)
  fi

  cat >/etc/nginx/sites-available/cscstocks <<EOF
map \$http_upgrade \$connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    listen [::]:80;
    server_name ${DOMAIN} www.${DOMAIN};
    return 301 https://${DOMAIN}\$request_uri;
}
${www_block}
server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name ${DOMAIN};

    ssl_certificate     /etc/letsencrypt/live/${DOMAIN}/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/${DOMAIN}/privkey.pem;
    include /etc/letsencrypt/options-ssl-nginx.conf;
    ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem;

    root /var/www/csx/frontend;
    index index.html;

    location /api/ {
        proxy_pass http://127.0.0.1:5233;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host \$host;
    }

    location /hub/ {
        proxy_pass http://127.0.0.1:5233;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 86400;
    }

    location /health {
        proxy_pass http://127.0.0.1:5233/health;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }

    location / {
        try_files \$uri \$uri/ /index.html;
    }
}
EOF
  nginx -t
  systemctl reload nginx
}

echo "==> nginx + TLS"
write_nginx_http

if [[ "${SKIP_CERTBOT:-0}" != 1 ]]; then
  vps_ip="$(public_ip)"
  apex_ip="$(dns_ip "$DOMAIN")"
  echo "VPS IPv4: ${vps_ip:-unknown}"
  echo "DNS ${DOMAIN}: ${apex_ip:-missing}"
  if [[ -z "$apex_ip" ]]; then
    echo "DNS for ${DOMAIN} is not resolving yet. Point an A record at this VPS and re-run." >&2
    exit 1
  fi
  if [[ -n "$vps_ip" && "$vps_ip" != "$apex_ip" ]]; then
    echo "DNS ${DOMAIN} -> ${apex_ip} does not match this VPS (${vps_ip})." >&2
    echo "If the domain is orange-clouded on Cloudflare, set the A record to DNS-only (grey cloud) and re-run." >&2
    echo "Otherwise fix the A record to this machine's public IP." >&2
    exit 1
  fi

  cert_args=(-d "$DOMAIN")
  www_ip="$(dns_ip "www.${DOMAIN}")"
  if [[ -n "$www_ip" ]]; then
    cert_args+=(-d "www.${DOMAIN}")
  fi

  if [[ ! -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" ]]; then
    certbot --nginx --non-interactive --agree-tos -m "$CERTBOT_EMAIL" --redirect "${cert_args[@]}"
  else
    echo "Certificate already exists for ${DOMAIN}"
  fi
  write_nginx_ssl
fi

echo "==> First build + start (frontend then API; can take several minutes)"
systemctl enable csx-frontend csx-api
systemctl start csx-frontend
systemctl start csx-api

echo
echo "============================================================"
echo "CSX is up."
echo "  Site:    https://${DOMAIN}"
echo "  Health:  https://${DOMAIN}/health"
echo
echo "Discord redirect (must match the portal exactly):"
echo "  https://${DOMAIN}/api/v1/auth/discord/callback"
echo
echo "Later updates (pull GitHub, rebuild if main moved):"
echo "  sudo systemctl start csx-deploy          # API + frontend"
echo "  sudo systemctl restart csx-api           # API only"
echo "  sudo systemctl restart csx-frontend      # SPA only"
echo
echo "Logs: journalctl -u csx-api -f"
echo "============================================================"
