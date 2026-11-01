#!/usr/bin/env bash
# Daily backup of the database and uploaded files. Run from root's cron.
# Credentials come from /etc/alshifa/backup.cnf (mode 600), never the command line.
# The same dump and restore steps were drilled on the development machine by scripts/backup-test.ps1.
# Copy the output off this server: a backup on the same disk does not survive losing the disk.
set -euo pipefail

DEST=/var/backups/alshifa
KEEP_DAYS=30
STAMP=$(date -u +%Y%m%dT%H%M%SZ)

mkdir -p "$DEST"
chmod 700 "$DEST"

mysqldump --defaults-extra-file=/etc/alshifa/backup.cnf \
  --single-transaction --routines --triggers --set-gtid-purged=OFF --default-character-set=utf8mb4 alshifa \
  | gzip > "$DEST/alshifa-db-$STAMP.sql.gz"

tar -czf "$DEST/alshifa-files-$STAMP.tar.gz" -C /var/lib/alshifa storage

gzip -t "$DEST/alshifa-db-$STAMP.sql.gz"
sha256sum "$DEST/alshifa-db-$STAMP.sql.gz" "$DEST/alshifa-files-$STAMP.tar.gz" > "$DEST/alshifa-$STAMP.sha256"

find "$DEST" -type f -mtime +"$KEEP_DAYS" -delete
