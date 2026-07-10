#!/bin/sh
set -eu
test "$(id -u)" = "0" || { echo "Run as root" >&2; exit 1; }
# Resolve paths from this script so installation does not depend on the caller's
# current directory.
SOURCE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
test -f "$SOURCE_DIR/dist/vault-to-procore" || { echo "Build dist/vault-to-procore first with deployment/build.sh" >&2; exit 1; }
id vault-to-procore >/dev/null 2>&1 || useradd --system --home /var/lib/vault-to-procore --shell /usr/sbin/nologin vault-to-procore
# Code, secrets, state, and logs have different ownership needs, so keep them in
# the standard Linux locations rather than putting everything under /opt.
install -d -o vault-to-procore -g vault-to-procore /var/lib/vault-to-procore /var/log/vault-to-procore
install -d /opt/vault-to-procore /etc/vault-to-procore
install -m 0755 "$SOURCE_DIR/dist/vault-to-procore" /opt/vault-to-procore/vault-to-procore
test -f /etc/vault-to-procore/vault-to-procore.env || install -m 0600 "$SOURCE_DIR/.env.example" /etc/vault-to-procore/vault-to-procore.env
install -m 0644 "$SOURCE_DIR/deployment/linux/vault-to-procore.service" /etc/systemd/system/vault-to-procore.service
install -m 0644 "$SOURCE_DIR/deployment/linux/vault-to-procore.timer" /etc/systemd/system/vault-to-procore.timer
systemctl daemon-reload
systemctl enable vault-to-procore.timer
echo "Installed. Edit /etc/vault-to-procore/vault-to-procore.env, validate, then start the timer."
