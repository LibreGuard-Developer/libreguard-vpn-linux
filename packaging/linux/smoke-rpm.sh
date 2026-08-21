#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RPM_PATH="${1:-}"
XVFB_PID=""
PACKAGE_INSTALLED=0

die() {
  echo "error: $*" >&2
  exit 1
}

find_latest_rpm() {
  local packages=()

  shopt -s nullglob
  packages=("$ROOT_DIR"/artifacts/libreguard-vpn-linux-*-*.x86_64.rpm)
  shopt -u nullglob

  if [[ "${#packages[@]}" -gt 0 ]]; then
    printf '%s\n' "${packages[@]}" | sort -V | tail -n 1
  fi
}

run_with_sudo() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  else
    command -v sudo >/dev/null 2>&1 \
      || die "sudo is required to install or remove the RPM package"
    sudo "$@"
  fi
}

cleanup_xvfb() {
  if [[ -n "$XVFB_PID" ]]; then
    kill "$XVFB_PID" >/dev/null 2>&1 || true
    wait "$XVFB_PID" >/dev/null 2>&1 || true
  fi
}

cleanup() {
  cleanup_xvfb
  if [[ "$PACKAGE_INSTALLED" -eq 1 ]] \
      && command -v dnf >/dev/null 2>&1 \
      && rpm -q libreguard-vpn-linux >/dev/null 2>&1; then
    echo "Removing LibreGuard after RPM smoke test."
    if [[ "$(id -u)" -eq 0 ]]; then
      dnf remove -y libreguard-vpn-linux >/dev/null 2>&1 || true
    elif command -v sudo >/dev/null 2>&1; then
      sudo dnf remove -y libreguard-vpn-linux >/dev/null 2>&1 || true
    fi
  fi
}

trap cleanup EXIT

run_webview_smoke() {
  local command=(
    /opt/libreguard-vpn-linux/libreguard-vpn-linux
    --webview-smoke
  )

  echo "Running installed GTK offscreen input/color smoke test under Xvfb/llvmpipe."
  if command -v xvfb-run >/dev/null 2>&1; then
    LIBREGUARD_WEBVIEW_MODE=gtk-offscreen \
      LIBGL_ALWAYS_SOFTWARE=1 \
      GALLIUM_DRIVER=llvmpipe \
      timeout 40s xvfb-run -a "${command[@]}"
    return
  fi

  if command -v Xvfb >/dev/null 2>&1; then
    Xvfb :99 -screen 0 1280x1024x24 -nolisten tcp &
    XVFB_PID="$!"
    LIBREGUARD_WEBVIEW_MODE=gtk-offscreen \
      LIBGL_ALWAYS_SOFTWARE=1 \
      GALLIUM_DRIVER=llvmpipe \
      DISPLAY=:99 \
      timeout 40s "${command[@]}"
    cleanup_xvfb
    XVFB_PID=""
    return
  fi

  echo "Xvfb was not found; skipping installed NativeWebView smoke test."
}

if [[ "$(uname -s)" != "Linux" ]]; then
  die "RPM package smoke tests must run on Linux"
fi

if [[ -z "$RPM_PATH" ]]; then
  RPM_PATH="$(find_latest_rpm)"
fi

if [[ ! -f "$RPM_PATH" ]]; then
  echo "Package not found; building it first."
  bash "$ROOT_DIR/packaging/linux/build-rpm.sh"
  RPM_PATH="$(find_latest_rpm)"
fi

[[ -n "${RPM_PATH:-}" && -f "$RPM_PATH" ]] \
  || die "package not found after build"

for command in rpm rpm2cpio cpio; do
  command -v "$command" >/dev/null 2>&1 || die "$command is required"
done

echo "Inspecting $RPM_PATH"
rpm -qpi "$RPM_PATH"
package_requirements="$(rpm -qpR "$RPM_PATH")"
package_contents="$(rpm -qpl "$RPM_PATH")"
package_metadata="$(rpm -qp --qf '[%{FILENAMES}\t%{FILEUSERNAME}\t%{FILEGROUPNAME}\t%{FILEMODES:perms}\n]' "$RPM_PATH")"
echo "$package_requirements"
echo "$package_contents"
echo "$package_metadata"

for dependency in \
    NetworkManager \
    NetworkManager-openvpn \
    NetworkManager-strongswan \
    openssl \
    openssl-libs \
    glibc \
    libgcc \
    libstdc++ \
    libicu \
    krb5-libs \
    ca-certificates \
    tzdata \
    polkit \
    iproute \
    webkit2gtk4.1 \
    libsecret \
    gnome-keyring \
    gnome-keyring-pam \
    xdg-utils \
    xorg-x11-server-Xwayland \
    google-noto-color-emoji-fonts \
    policycoreutils \
    libselinux-utils \
    acl; do
  echo "$package_requirements" | grep -Fxq "$dependency" \
    || die "RPM is missing dependency: $dependency"
done

for path in \
    /opt/libreguard-vpn-linux/libreguard-vpn-linux \
    /opt/libreguard-vpn-linux/build-info.json \
    /usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair \
    /etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle \
    /usr/share/applications/libreguard-vpn-linux.desktop \
    /usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png \
    /usr/share/polkit-1/actions/net.libreguard.vpn.linux.repair-ikev2-routing.policy \
    /usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil; do
  echo "$package_contents" | grep -Fxq "$path" \
    || die "RPM is missing payload path: $path"
done

echo "$package_metadata" \
  | grep -Eq "^/opt/libreguard-vpn-linux/libreguard-vpn-linux[[:space:]]+root[[:space:]]+root[[:space:]]+-rwxr-xr-x$"
echo "$package_metadata" \
  | grep -Eq "^/usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair[[:space:]]+root[[:space:]]+root[[:space:]]+-rwxr-xr-x$"
echo "$package_metadata" \
  | grep -Eq "^/usr/share/polkit-1/actions/net\\.libreguard\\.vpn\\.linux\\.repair-ikev2-routing\\.policy[[:space:]]+root[[:space:]]+root[[:space:]]+-rw-r--r--$"
echo "$package_metadata" \
  | grep -Eq "^/usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora\\.cil[[:space:]]+root[[:space:]]+root[[:space:]]+-rw-r--r--$"

if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$ROOT_DIR/packaging/linux/libreguard-vpn-linux.desktop"
fi

if command -v dnf >/dev/null 2>&1; then
  echo "Installing package with DNF for smoke verification."
  run_with_sudo dnf install -y "$RPM_PATH"
  PACKAGE_INSTALLED=1
  test -x /opt/libreguard-vpn-linux/libreguard-vpn-linux
  test -f /opt/libreguard-vpn-linux/build-info.json
  grep -Fq '"dirty": false' /opt/libreguard-vpn-linux/build-info.json
  test -f /usr/share/applications/libreguard-vpn-linux.desktop
  test -f /usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png
  test -x /usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair
  test -f /usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil
  rpm -q libreguard-vpn-linux
  rpm -q acl
  rpm -q webkit2gtk4.1
  rpm -q xdg-utils
  rpm -q xorg-x11-server-Xwayland
  rpm -q gnome-keyring
  rpm -q gnome-keyring-pam
  run_with_sudo semodule -l | grep -q '^libreguard_ikev2_fedora\([[:space:]]\|$\)'
  run_webview_smoke
  run_with_sudo dnf remove -y libreguard-vpn-linux
  PACKAGE_INSTALLED=0
  if run_with_sudo semodule -l | grep -q '^libreguard_ikev2_fedora\([[:space:]]\|$\)'; then
    die "Fedora IKEv2 SELinux policy remained installed after package removal"
  fi
else
  echo "Skipping install smoke: dnf is required."
fi
