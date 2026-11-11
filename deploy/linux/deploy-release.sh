#!/usr/bin/env bash
# Installs a new version of the online copy. Code moves only by this script, never through the data sync.
# Run as root on the Contabo server with the release built at the hospital/dev machine:
#   dotnet publish backend/src/AlShifa.Api -c Release -r linux-x64 --self-contained -o out  (with the built frontend in out/wwwroot)
#   tar -czf alshifa-<commit>.tar.gz -C out .   then copy it to the server.
#   deploy/linux/deploy-release.sh /tmp/alshifa-<commit>.tar.gz <sha256>
#
# Order: check the archive, unpack beside the running version, back up the online database, migrate, switch, check.
# Any failure puts the previous version and database back. Other sites on this server are not touched.
set -euo pipefail

archive=${1:?archive path}
expected=${2:?sha256 of the archive}
stamp=$(date -u +%Y%m%dT%H%M%SZ)
release=/opt/alshifa/releases/$stamp
backup_dir=/var/backups/alshifa/deployments/$stamp
db=alshifa_mirror
container=alshifa-mysql

test "$(sha256sum "$archive" | awk '{print $1}')" = "$expected"
# shellcheck disable=SC1091
. /etc/alshifa/mirror-owner.env
test -n "${ALSHIFA_MIRROR_OWNER:-}"

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

root_mysql() {
  docker exec -i "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot'
}

# The online copy can always be rebuilt from the hospital, but a backup makes going back immediate.
docker exec "$container" sh -c "MYSQL_PWD=\"\$MYSQL_ROOT_PASSWORD\" mysqldump -uroot --single-transaction --quick --no-tablespaces --set-gtid-purged=OFF $db" | gzip > "$backup_dir/$db.sql.gz"
gzip -t "$backup_dir/$db.sql.gz"

switched=0
rollback() {
  local rc=$?
  [ "$switched" = 1 ] || { echo "Deploy stopped before the switch; the running version is unchanged."; return "$rc"; }
  set +e
  echo "DEPLOY FAILED ($rc): restoring $previous and the database backup"
  systemctl stop alshifa-api
  printf 'DROP DATABASE `%s`; CREATE DATABASE `%s` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;\n' "$db" "$db" | root_mysql
  gunzip -c "$backup_dir/$db.sql.gz" | docker exec -i "$container" sh -c "MYSQL_PWD=\"\$MYSQL_ROOT_PASSWORD\" mysql -uroot $db"
  ln -sfn "$previous" /opt/alshifa/current.next && mv -Tf /opt/alshifa/current.next /opt/alshifa/current
  systemctl start alshifa-api
  return "$rc"
}
trap rollback EXIT

systemctl stop alshifa-api
switched=1
(
  cd "$release"
  sudo -u alshifa env ASPNETCORE_ENVIRONMENT=Production Mirror__Enabled=true \
    ConnectionStrings__Default="$ALSHIFA_MIRROR_OWNER" \
    Storage__Path=/var/lib/alshifa/storage DataProtection__KeysPath=/var/lib/alshifa/keys \
    ./AlShifa.Api --migrate --backup-taken
)
# A new migration may recreate AspNetUsers; the read-only account's sign-in columns must stay writable.
printf "GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON \`%s\`.\`AspNetUsers\` TO 'alshifa_mirror_ro'@'%%';\n" "$db" | root_mysql

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

trap - EXIT
echo "Deployed $release (previous: $previous, database backup: $backup_dir)."
echo "The hospital server re-sends everything once after a version change; watch Settings > Backup there."
