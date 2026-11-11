#!/usr/bin/env bash
# The SFTP account that receives the hospital's encrypted nightly backups on the Contabo server. Run as root.
#   deploy/linux/offsite-backup-account.sh "ssh-ed25519 AAAA... alshifa-hospital-backup"
#
# The account can only use SFTP inside /var/backups/alshifa-offsite (chroot, outside every web root): no shell, no forwarding.
# Files arrive already encrypted with the hospital's backup password, which this server never has.
# sshd's configuration is checked with `sshd -t` before the reload; a mistake leaves the running sshd as it was.
set -euo pipefail

public_key=${1:?public key line printed by online.ps1}
user=alshifa-backup
base=/var/backups/alshifa-offsite
drop_in=/etc/ssh/sshd_config.d/alshifa-backup.conf

id "$user" >/dev/null 2>&1 || useradd --system --home-dir "$base" --shell /usr/sbin/nologin "$user"

# A chroot must be owned by root and not writable by the user; uploads go to the folder inside it.
install -d -o root -g root -m 755 "$base"
install -d -o "$user" -g "$user" -m 700 "$base/upload"
install -d -o root -g root -m 755 /etc/ssh/authorized_keys
printf 'restrict %s\n' "$public_key" > "/etc/ssh/authorized_keys/$user"
chmod 644 "/etc/ssh/authorized_keys/$user"

grep -q '^Include /etc/ssh/sshd_config.d/\*.conf' /etc/ssh/sshd_config || { echo "sshd_config does not include sshd_config.d; add the Match block by hand." >&2; exit 1; }
cat > "$drop_in" <<EOF
Match User $user
    AuthorizedKeysFile /etc/ssh/authorized_keys/%u
    ChrootDirectory $base
    ForceCommand internal-sftp
    PasswordAuthentication no
    AllowTcpForwarding no
    X11Forwarding no
    PermitTunnel no
EOF

if sshd -t; then
  systemctl reload ssh 2>/dev/null || systemctl reload sshd
else
  rm -f "$drop_in"
  echo "sshd rejected the configuration; nothing was changed." >&2
  exit 1
fi

echo "Ready. Host key line for online.ps1 -OffsiteHostKey (compare with: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub):"
echo "$(hostname -I | awk '{print $1}') $(cat /etc/ssh/ssh_host_ed25519_key.pub | awk '{print $1, $2}')"
