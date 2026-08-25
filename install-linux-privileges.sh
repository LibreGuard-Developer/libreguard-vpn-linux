#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HELPER_SOURCE="$SCRIPT_DIR/packaging/linux/helpers/libreguard-ikev2-route-repair"
IPV6_HELPER_SOURCE="$SCRIPT_DIR/packaging/linux/helpers/libreguard-ipv6-leak-protection"
VPN_RECOVERY_HELPER_SOURCE="$SCRIPT_DIR/packaging/linux/helpers/libreguard-vpn-recovery"
DISPATCHER_SOURCE="$SCRIPT_DIR/packaging/linux/dispatcher/90-libreguard-vpn-lifecycle"
VPN_RECOVERY_SERVICE_SOURCE="$SCRIPT_DIR/packaging/linux/systemd/libreguard-vpn-recovery.service"
POLICY_SOURCE="$SCRIPT_DIR/packaging/linux/polkit/net.libreguard.vpn.linux.repair-ikev2-routing.policy"

HELPER_TARGET_DIR="/usr/libexec/libreguard-vpn-linux"
HELPER_TARGET="$HELPER_TARGET_DIR/libreguard-ikev2-route-repair"
IPV6_HELPER_TARGET="$HELPER_TARGET_DIR/libreguard-ipv6-leak-protection"
VPN_RECOVERY_HELPER_TARGET="$HELPER_TARGET_DIR/libreguard-vpn-recovery"
DISPATCHER_TARGET_DIR="/usr/lib/NetworkManager/dispatcher.d"
DISPATCHER_TARGET="$DISPATCHER_TARGET_DIR/90-libreguard-vpn-lifecycle"
PRE_UP_DISPATCHER_TARGET_DIR="$DISPATCHER_TARGET_DIR/pre-up.d"
PRE_UP_DISPATCHER_TARGET="$PRE_UP_DISPATCHER_TARGET_DIR/90-libreguard-vpn-lifecycle"
SYSTEM_PRE_UP_DISPATCHER_TARGET_DIR="/etc/NetworkManager/dispatcher.d/pre-up.d"
SYSTEM_PRE_UP_DISPATCHER_TARGET="$SYSTEM_PRE_UP_DISPATCHER_TARGET_DIR/90-libreguard-vpn-lifecycle"
POLICY_TARGET_DIR="/usr/share/polkit-1/actions"
POLICY_TARGET="$POLICY_TARGET_DIR/net.libreguard.vpn.linux.repair-ikev2-routing.policy"
SYSTEMD_UNIT_TARGET_DIR="/usr/lib/systemd/system"
VPN_RECOVERY_SERVICE_TARGET="$SYSTEMD_UNIT_TARGET_DIR/libreguard-vpn-recovery.service"

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this installer with sudo so LibreGuard can install its NetworkManager dispatcher helpers." >&2
  exit 1
fi

for asset in "$HELPER_SOURCE" "$IPV6_HELPER_SOURCE" "$VPN_RECOVERY_HELPER_SOURCE" "$DISPATCHER_SOURCE" "$VPN_RECOVERY_SERVICE_SOURCE" "$POLICY_SOURCE"; do
  if [[ ! -f "$asset" ]]; then
    echo "Missing privilege asset: $asset" >&2
    exit 1
  fi
done

NETPLAN_FILE="/etc/netplan/01-network-manager-all.yaml"
if [[ -e "$NETPLAN_FILE" ]]; then
  if ! chown root:root "$NETPLAN_FILE" || ! chmod 0600 "$NETPLAN_FILE"; then
    echo "error: could not secure $NETPLAN_FILE (expected root:root, mode 0600)" >&2
    exit 1
  fi
fi

install -d -m 0755 "$HELPER_TARGET_DIR"
install -d -m 0755 "$DISPATCHER_TARGET_DIR"
install -d -m 0755 "$PRE_UP_DISPATCHER_TARGET_DIR"
install -d -m 0755 "$SYSTEM_PRE_UP_DISPATCHER_TARGET_DIR"
install -d -m 0755 "$POLICY_TARGET_DIR"
install -d -m 0755 "$SYSTEMD_UNIT_TARGET_DIR"
install -o root -g root -m 0755 "$HELPER_SOURCE" "$HELPER_TARGET"
install -o root -g root -m 0755 "$IPV6_HELPER_SOURCE" "$IPV6_HELPER_TARGET"
install -o root -g root -m 0755 "$VPN_RECOVERY_HELPER_SOURCE" "$VPN_RECOVERY_HELPER_TARGET"
install -o root -g root -m 0755 "$DISPATCHER_SOURCE" "$DISPATCHER_TARGET"
install -o root -g root -m 0755 "$DISPATCHER_SOURCE" "$PRE_UP_DISPATCHER_TARGET"
install -o root -g root -m 0755 "$DISPATCHER_SOURCE" "$SYSTEM_PRE_UP_DISPATCHER_TARGET"
install -o root -g root -m 0644 "$POLICY_SOURCE" "$POLICY_TARGET"
install -o root -g root -m 0644 "$VPN_RECOVERY_SERVICE_SOURCE" "$VPN_RECOVERY_SERVICE_TARGET"

if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  systemctl enable libreguard-vpn-recovery.service
fi

echo "Installed LibreGuard IKEv2 route repair helper to $HELPER_TARGET"
echo "Installed LibreGuard IPv6 leak-protection helper to $IPV6_HELPER_TARGET"
echo "Installed LibreGuard VPN boot-recovery helper to $VPN_RECOVERY_HELPER_TARGET"
echo "Installed LibreGuard NetworkManager dispatcher to $DISPATCHER_TARGET"
echo "Installed LibreGuard NetworkManager pre-up dispatcher to $PRE_UP_DISPATCHER_TARGET"
echo "Installed LibreGuard system pre-up dispatcher to $SYSTEM_PRE_UP_DISPATCHER_TARGET"
echo "Installed LibreGuard route-repair recovery policy to $POLICY_TARGET"
echo "Enabled LibreGuard VPN boot recovery for stale VPN/DNS state."
echo "LibreGuard can now manage IKEv2 routing, IPv6 blocking, and browser DNS leak protection without connection-time password prompts."
