#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

find_latest_deb() {
  local packages=()

  shopt -s nullglob
  packages=("$ROOT_DIR"/artifacts/libreguard-vpn-linux_*_amd64.deb)
  shopt -u nullglob

  if [[ "${#packages[@]}" -gt 0 ]]; then
    printf '%s\n' "${packages[@]}" | sort -V | tail -n 1
  fi
}

DEB_PATH="${1:-}"

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "Debian package smoke tests must run on Linux." >&2
  exit 1
fi

if [[ -z "$DEB_PATH" ]]; then
  DEB_PATH="$(find_latest_deb)"
fi

if [[ ! -f "$DEB_PATH" ]]; then
  echo "Package not found; building it first."
  bash "$ROOT_DIR/packaging/linux/build-deb.sh"
  DEB_PATH="$(find_latest_deb)"
fi

if [[ -z "${DEB_PATH:-}" || ! -f "$DEB_PATH" ]]; then
  echo "Package not found after build." >&2
  exit 1
fi

echo "Inspecting $DEB_PATH"
dpkg-deb --info "$DEB_PATH"
dpkg-deb --contents "$DEB_PATH"

dpkg-deb --contents "$DEB_PATH" | grep -q "./opt/libreguard-vpn-linux/libreguard-vpn-linux"
dpkg-deb --contents "$DEB_PATH" | grep -q "./opt/libreguard-vpn-linux/build-info.json"
dpkg-deb --contents "$DEB_PATH" | grep -q "./usr/share/applications/libreguard-vpn-linux.desktop"
dpkg-deb --contents "$DEB_PATH" | grep -q "./usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png"
dpkg-deb --contents "$DEB_PATH" | grep -Eq "^drwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./opt/libreguard-vpn-linux/$"
dpkg-deb --contents "$DEB_PATH" | grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./opt/libreguard-vpn-linux/libhostfxr\\.so$"
dpkg-deb --contents "$DEB_PATH" | grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./opt/libreguard-vpn-linux/libreguard-vpn-linux$"
dpkg-deb --contents "$DEB_PATH" | grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair$"
dpkg-deb --contents "$DEB_PATH" | grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./etc/NetworkManager/dispatcher\\.d/pre-up\\.d/90-libreguard-vpn-lifecycle$"
dpkg-deb --contents "$DEB_PATH" | grep -Eq "^-rw-r--r--[[:space:]]+root/root[[:space:]].*\\./usr/share/polkit-1/actions/net\\.libreguard\\.vpn\\.linux\\.repair-ikev2-routing\\.policy$"

if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$ROOT_DIR/packaging/linux/libreguard-vpn-linux.desktop"
fi

if command -v sudo >/dev/null 2>&1 && command -v apt-get >/dev/null 2>&1; then
  echo "Installing package with apt-get for smoke verification."
  sudo apt-get update
  sudo apt-get install -y --no-install-recommends "$DEB_PATH"
  test -x /opt/libreguard-vpn-linux/libreguard-vpn-linux
  test -f /opt/libreguard-vpn-linux/build-info.json
  grep -Fq '"dirty": false' /opt/libreguard-vpn-linux/build-info.json
  test -f /usr/share/applications/libreguard-vpn-linux.desktop
  test -f /usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png
  dpkg -s libreguard-vpn-linux
  if ! dpkg -s libwpewebkit-2.0-1 >/dev/null 2>&1 && ! dpkg -s libwebkit2gtk-4.1-0 >/dev/null 2>&1; then
    echo "No supported native WebView runtime was installed." >&2
    exit 1
  fi
  dpkg -s ca-certificates
  dpkg -s xdg-utils
  dpkg -s gnome-keyring
  dpkg -s libpam-gnome-keyring
  if command -v xvfb-run >/dev/null 2>&1; then
    echo "Running installed GTK offscreen input/color smoke test under Xvfb/llvmpipe."
    LIBREGUARD_WEBVIEW_MODE=gtk-offscreen \
      LIBGL_ALWAYS_SOFTWARE=1 \
      GALLIUM_DRIVER=llvmpipe \
      timeout 40s xvfb-run -a /opt/libreguard-vpn-linux/libreguard-vpn-linux --webview-smoke
  else
    echo "xvfb-run not found; skipping installed NativeWebView smoke test."
  fi
  sudo apt-get remove -y libreguard-vpn-linux
else
  echo "Skipping install smoke: sudo and apt-get are required."
fi
