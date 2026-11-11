#!/usr/bin/env bash
# One-time switch of the Contabo server (shafa.novatechsoft.com) to the online read-only copy. Run as root.
#   deploy/linux/mirror-setup.sh /tmp/alshifa-linux-<commit>.tar.gz <sha256>
# Later versions use deploy-release.sh.
#
# - The old database `alshifa` is NOT touched: it stays as the archive of the old online version, and is backed up first.
# - A new database `alshifa_mirror` receives the hospital's data through sync.
# - Three accounts: owner (migrations only, used by deploy-release.sh), sync (writes rows, used only by the signed sync endpoint),
#   read-only (everything the web application does: SELECT, plus three sign-in counter columns of AspNetUsers).
# - Secrets go to /etc/alshifa (root, 600). The sync key is written to /etc/alshifa/sync-key.txt and never printed.
# - Nginx gets a larger body limit for the signed sync path only; the configuration is tested before the reload.
# Any failure puts the previous version, the Nginx site and the service settings back and removes what this script created.
set -euo pipefail

archive=${1:?archive path}
expected=${2:?sha256 of the archive}
db=alshifa_mirror
env_file=/etc/alshifa/mirror.env
owner_file=/etc/alshifa/mirror-owner.env
key_file=/etc/alshifa/sync-key.txt
drop_in=/etc/systemd/system/alshifa-api.service.d/mirror.conf
site=/etc/nginx/sites-enabled/shafa.novatechsoft.com
container=alshifa-mysql
mysql_port=3316
stamp=$(date -u +%Y%m%dT%H%M%SZ)
release=/opt/alshifa/releases/$stamp-mirror
backup_dir=/var/backups/alshifa/deployments/$stamp-mirror-setup

root_mysql() {
  docker exec -i "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot'
}

if [ -e "$env_file" ]; then
  echo "$env_file exists; setup was already done. Use deploy-release.sh for a new version." >&2
  exit 1
fi
if [ -n "$(echo "SHOW DATABASES LIKE '$db';" | root_mysql)" ]; then
  echo "Database $db already exists; remove it deliberately to start over." >&2
  exit 1
fi

test "$(sha256sum "$archive" | awk '{print $1}')" = "$expected"
previous=$(readlink -f /opt/alshifa/current)
mkdir -p "$release"
# Database dumps hold patient data: root only.
install -d -m 700 "$backup_dir"
tar -xzf "$archive" -C "$release"
# Built on Windows: the archive carries no Unix modes. Nothing may be writable by others, and the program must run.
chmod -R u=rwX,go=rX "$release"
chmod 755 "$release/AlShifa.Api"
test -x "$release/AlShifa.Api"
test -f "$release/wwwroot/index.html"
chown -R root:root "$release"

# The old online version and its database stay exactly as they are; this copy makes going back immediate.
docker exec "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysqldump -uroot --single-transaction --quick --no-tablespaces --set-gtid-purged=OFF alshifa' | gzip > "$backup_dir/alshifa.sql.gz"
gzip -t "$backup_dir/alshifa.sql.gz"
cp -L "$site" "$backup_dir/nginx-site.conf"

created=0
rollback() {
  local rc=$?
  set +e
  echo "SETUP FAILED ($rc): putting the previous version back"
  ln -sfn "$previous" /opt/alshifa/current.next && mv -Tf /opt/alshifa/current.next /opt/alshifa/current
  rm -f "$drop_in"
  systemctl daemon-reload
  cat "$backup_dir/nginx-site.conf" > "$(readlink -f "$site")" && nginx -t 2>/dev/null && systemctl reload nginx
  if [ "$created" = 1 ]; then
    printf 'DROP DATABASE IF EXISTS `%s`; DROP USER IF EXISTS alshifa_mirror_owner, alshifa_mirror_sync, alshifa_mirror_ro;\n' "$db" | root_mysql
    rm -f "$env_file" "$owner_file" "$key_file"
  fi
  systemctl restart alshifa-api
  return "$rc"
}
trap rollback EXIT

owner_pass=$(openssl rand -hex 24)
sync_pass=$(openssl rand -hex 24)
ro_pass=$(openssl rand -hex 24)
sync_key=$(openssl rand -base64 64 | tr -dc 'A-Za-z0-9' | head -c 48)
test "${#sync_key}" = 48

created=1
root_mysql <<SQL
CREATE DATABASE \`$db\` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'alshifa_mirror_owner'@'%' IDENTIFIED BY '$owner_pass';
GRANT ALL PRIVILEGES ON \`$db\`.* TO 'alshifa_mirror_owner'@'%';
CREATE USER 'alshifa_mirror_sync'@'%' IDENTIFIED BY '$sync_pass';
GRANT SELECT, INSERT, UPDATE, DELETE ON \`$db\`.* TO 'alshifa_mirror_sync'@'%';
CREATE USER 'alshifa_mirror_ro'@'%' IDENTIFIED BY '$ro_pass';
GRANT SELECT ON \`$db\`.* TO 'alshifa_mirror_ro'@'%';
SQL

conn() { printf 'Server=127.0.0.1;Port=%s;Database=%s;User=%s;Password=%s;' "$mysql_port" "$db" "$1" "$2"; }

umask 077
mkdir -p /etc/alshifa
# Quoted: connection strings hold ";", which a shell sourcing the file would read as the end of a command. systemd removes the quotes.
cat > "$env_file" <<EOF
Mirror__Enabled="true"
Mirror__SyncKey="$sync_key"
Mirror__SyncConnection="$(conn alshifa_mirror_sync "$sync_pass")"
ConnectionStrings__Default="$(conn alshifa_mirror_ro "$ro_pass")"
EOF
# The owner account is for deployments only and never reaches the running service.
printf 'ALSHIFA_MIRROR_OWNER="%s"\n' "$(conn alshifa_mirror_owner "$owner_pass")" > "$owner_file"
printf '%s\n' "$sync_key" > "$key_file"
chown root:root "$env_file" "$owner_file" "$key_file"
chmod 600 "$env_file" "$owner_file" "$key_file"

# Schema, with the owner account. The read-only grant on the sign-in columns needs the table to exist.
(
  cd "$release"
  sudo -u alshifa env ASPNETCORE_ENVIRONMENT=Production Mirror__Enabled=true \
    ConnectionStrings__Default="$(conn alshifa_mirror_owner "$owner_pass")" \
    Storage__Path=/var/lib/alshifa/storage DataProtection__KeysPath=/var/lib/alshifa/keys \
    ./AlShifa.Api --migrate --backup-taken
)
printf "GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON \`%s\`.\`AspNetUsers\` TO 'alshifa_mirror_ro'@'%%';\n" "$db" | root_mysql

# Sync batches are compressed but can still pass the 11 MB page limit; only the signed sync path gets more.
if ! grep -q 'location /api/sync/mirror/' "$site"; then
  python3 - "$site" <<'PY'
import sys
path = sys.argv[1]
text = open(path).read()
block = """    # Signed batches from the hospital server. The application checks the signature and its own 64 MB limit.
    location /api/sync/mirror/ {
        client_max_body_size 64m;
        proxy_read_timeout 300s;
        include /etc/nginx/alshifa-proxy.conf;
    }

"""
anchor = "    location / {\n        include /etc/nginx/alshifa-proxy.conf;"
index = text.rfind(anchor)
if index < 0:
    sys.exit("Nginx site layout not recognised; add the sync location by hand.")
open(path, "w").write(text[:index] + block + text[index:])
PY
fi
nginx -t
systemctl reload nginx

# The service reads mirror.env after api.env, so its values win. api.env (the old database) stays for going back.
mkdir -p "$(dirname "$drop_in")"
cat > "$drop_in" <<'EOF'
[Service]
EnvironmentFile=/etc/alshifa/mirror.env
EOF
systemctl daemon-reload

systemctl stop alshifa-api
ln -sfn "$release" /opt/alshifa/current.next
mv -Tf /opt/alshifa/current.next /opt/alshifa/current
systemctl start alshifa-api

ok=0
for _ in $(seq 1 45); do
  if curl -fsS http://127.0.0.1:5080/health | grep -q Healthy; then ok=1; break; fi
  sleep 1
done
test "$ok" = 1
curl -fsS --resolve shafa.novatechsoft.com:443:127.0.0.1 https://shafa.novatechsoft.com/ | grep -qi '<!doctype html>'
# The sync path exists and refuses an unsigned request.
test "$(curl -s -o /dev/null -w '%{http_code}' --resolve shafa.novatechsoft.com:443:127.0.0.1 https://shafa.novatechsoft.com/api/sync/mirror/state)" = 401

trap - EXIT
echo "Online copy ready: $release (previous: $previous, backups: $backup_dir)."
echo "Sync key for the hospital server (online.ps1 -SyncKey): root-only file $key_file"
