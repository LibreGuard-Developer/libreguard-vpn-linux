#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONTROL_SOURCE="$ROOT_DIR/packaging/linux/deb/control"
RPM_SPEC_SOURCE="$ROOT_DIR/packaging/linux/rpm/libreguard-vpn-linux.spec"
PUBLISH_DIR="$ROOT_DIR/bin/Release/net10.0/linux-x64/publish"
PACKAGE_ICON_SOURCE="$ROOT_DIR/Resources/LibreGuard_logo_login_rounded12.png"

OUTPUT_DIR="${OUTPUT_DIR:-artifacts}"
PACKAGE_VERSION="${VERSION:-}"
RPM_PACKAGE_RELEASE="${RPM_RELEASE:-1}"
PACKAGE_FORMAT="all"
SKIP_BUILD_DEPS=0
INSTALL_PACKAGE=0
ALLOW_MISSING_GOOGLE_OAUTH_CLIENT_ID="${ALLOW_MISSING_GOOGLE_OAUTH_CLIENT_ID:-0}"
ALLOW_DIRTY_SOURCE="${ALLOW_DIRTY_SOURCE:-0}"

usage() {
  cat <<EOF
Usage: bash ./packaging/linux/build-packages.sh [options]

Publish LibreGuard once and build Debian and/or RPM packages.

Options:
  --format <deb|rpm|all>                      Package format. Defaults to all.
  --version <version>                         Package version. Defaults to packaging/linux/deb/control.
  --rpm-release <release>                     RPM release. Defaults to 1.
  --output-dir <dir>                          Output directory. Defaults to artifacts.
  --skip-build-deps                           Do not install build prerequisites.
  --allow-missing-google-oauth-client-id      Allow a CI/test package without Google OAuth client ID injection.
  --allow-dirty-source                        Allow a development package from modified or untracked source.
  --install                                   Install and verify one locally built package; invalid with --format all.
  --help                                      Show this help.

Environment:
  VERSION=1.2.3
  RPM_RELEASE=1
  OUTPUT_DIR=artifacts
  ALLOW_MISSING_GOOGLE_OAUTH_CLIENT_ID=1
  ALLOW_DIRTY_SOURCE=1
EOF
}

die() {
  echo "error: $*" >&2
  exit 1
}

require_clean_source() {
  local source_status

  command -v git >/dev/null 2>&1 \
    || die "git is required to produce a reproducible package identity"
  git -C "$ROOT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1 \
    || die "package source must be a Git worktree"

  source_status="$(git -C "$ROOT_DIR" status --porcelain --untracked-files=normal)"
  if [[ -z "$source_status" ]]; then
    return
  fi

  if [[ "$ALLOW_DIRTY_SOURCE" == "1" ]]; then
    echo "warning: building a development package from dirty source because --allow-dirty-source was provided" >&2
    return
  fi

  echo "Dirty source paths:" >&2
  printf '%s\n' "$source_status" >&2
  die "refusing to build from tracked or untracked source changes; commit/stash them or pass --allow-dirty-source for a development-only package"
}

wants_deb() {
  [[ "$PACKAGE_FORMAT" == "deb" || "$PACKAGE_FORMAT" == "all" ]]
}

wants_rpm() {
  [[ "$PACKAGE_FORMAT" == "rpm" || "$PACKAGE_FORMAT" == "all" ]]
}

read_control_version() {
  local line
  local version

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == Version:* ]]; then
      version="${line#Version:}"
      version="${version#"${version%%[![:space:]]*}"}"
      echo "$version"
      return
    fi
  done < "$CONTROL_SOURCE"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --format)
      [[ $# -ge 2 ]] || die "--format requires a value"
      PACKAGE_FORMAT="$2"
      shift 2
      ;;
    --format=*)
      PACKAGE_FORMAT="${1#*=}"
      shift
      ;;
    --version)
      [[ $# -ge 2 ]] || die "--version requires a value"
      PACKAGE_VERSION="$2"
      shift 2
      ;;
    --version=*)
      PACKAGE_VERSION="${1#*=}"
      shift
      ;;
    --rpm-release)
      [[ $# -ge 2 ]] || die "--rpm-release requires a value"
      RPM_PACKAGE_RELEASE="$2"
      shift 2
      ;;
    --rpm-release=*)
      RPM_PACKAGE_RELEASE="${1#*=}"
      shift
      ;;
    --output-dir)
      [[ $# -ge 2 ]] || die "--output-dir requires a value"
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --output-dir=*)
      OUTPUT_DIR="${1#*=}"
      shift
      ;;
    --skip-build-deps)
      SKIP_BUILD_DEPS=1
      shift
      ;;
    --allow-missing-google-oauth-client-id)
      ALLOW_MISSING_GOOGLE_OAUTH_CLIENT_ID=1
      shift
      ;;
    --allow-dirty-source)
      ALLOW_DIRTY_SOURCE=1
      shift
      ;;
    --install)
      INSTALL_PACKAGE=1
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      die "unknown option: $1"
      ;;
  esac
done

case "$PACKAGE_FORMAT" in
  deb|rpm|all) ;;
  *) die "--format must be deb, rpm, or all" ;;
esac

if [[ "$INSTALL_PACKAGE" -eq 1 && "$PACKAGE_FORMAT" == "all" ]]; then
  die "--install requires --format deb or --format rpm"
fi
if [[ "$INSTALL_PACKAGE" -eq 1 && "$ALLOW_DIRTY_SOURCE" == "1" ]]; then
  die "--install cannot be combined with --allow-dirty-source; installed smoke-test packages must report dirty=false"
fi
case "$ALLOW_DIRTY_SOURCE" in
  0|1) ;;
  *) die "ALLOW_DIRTY_SOURCE must be 0 or 1" ;;
esac

if [[ "$(uname -s)" != "Linux" ]]; then
  die "Linux packages must be built on Linux."
fi

machine_arch="$(uname -m)"
if [[ "$machine_arch" != "x86_64" && "$machine_arch" != "amd64" ]]; then
  die "This release script currently supports x86_64/amd64 only; found $machine_arch."
fi

if [[ -z "$PACKAGE_VERSION" ]]; then
  PACKAGE_VERSION="$(read_control_version)"
fi

[[ -n "$PACKAGE_VERSION" ]] || die "package version is empty"
[[ -n "$OUTPUT_DIR" ]] || die "output directory is empty"
if wants_deb; then
  [[ "$PACKAGE_VERSION" =~ ^[0-9][0-9A-Za-z.+:~_-]*$ ]] \
    || die "invalid Debian package version: $PACKAGE_VERSION"
fi
if wants_rpm; then
  [[ "$PACKAGE_VERSION" =~ ^[0-9][0-9A-Za-z.+_~]*$ ]] \
    || die "invalid RPM version: $PACKAGE_VERSION (RPM versions cannot contain '-' or ':')"
  [[ "$RPM_PACKAGE_RELEASE" =~ ^[0-9][0-9A-Za-z.+_~]*$ ]] \
    || die "invalid RPM release: $RPM_PACKAGE_RELEASE"
fi

require_clean_source

case "$OUTPUT_DIR" in
  /*) ;;
  *) OUTPUT_DIR="$ROOT_DIR/$OUTPUT_DIR" ;;
esac

DEB_PATH="$OUTPUT_DIR/libreguard-vpn-linux_${PACKAGE_VERSION}_amd64.deb"
RPM_PATH="$OUTPUT_DIR/libreguard-vpn-linux-${PACKAGE_VERSION}-${RPM_PACKAGE_RELEASE}.x86_64.rpm"
TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
PACKAGE_ROOT="$(mktemp -d "$TEMP_ROOT/libreguard-vpn-linux.deb.XXXXXX")"
BUILD_ROOT="$(mktemp -d "$TEMP_ROOT/libreguard-vpn-linux.build.XXXXXX")"
RPM_TOPDIR="$(mktemp -d "$TEMP_ROOT/libreguard-vpn-linux.rpm.XXXXXX")"
PAYLOAD_ROOT="$BUILD_ROOT/payload"

cleanup_temp_roots() {
  local target
  for target in "$PACKAGE_ROOT" "$BUILD_ROOT" "$RPM_TOPDIR"; do
    case "$target" in
      "$TEMP_ROOT"/libreguard-vpn-linux.deb.*|\
      "$TEMP_ROOT"/libreguard-vpn-linux.build.*|\
      "$TEMP_ROOT"/libreguard-vpn-linux.rpm.*)
        rm -rf -- "$target"
        ;;
      *)
        echo "warning: refusing to clear unexpected temporary directory: $target" >&2
        ;;
    esac
  done
}

trap cleanup_temp_roots EXIT

run_with_sudo() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  else
    command -v sudo >/dev/null 2>&1 \
      || die "sudo is required to install build dependencies or a package"
    sudo "$@"
  fi
}

apt_package_is_installed() {
  dpkg-query -W -f='${Status}' "$1" 2>/dev/null | grep -q "install ok installed"
}

rpm_package_is_installed() {
  rpm -q "$1" >/dev/null 2>&1
}

has_dotnet_10_sdk() {
  local sdk_list
  command -v dotnet >/dev/null 2>&1 || return 1
  sdk_list="$(dotnet --list-sdks 2>/dev/null)" || return 1
  grep -Eq '^10\.' <<< "$sdk_list"
}

dpkg_deb_supports_root_owner_group() {
  local help_text
  help_text="$(dpkg-deb --help 2>&1)" || return 1
  grep -Fq -- "--root-owner-group" <<< "$help_text"
}

install_build_dependencies_with_apt() {
  local packages=(bash coreutils diffutils findutils desktop-file-utils ca-certificates)
  local missing=()
  local package

  if wants_deb; then
    packages+=(dpkg-dev)
  fi
  if wants_rpm; then
    packages+=(rpm cpio)
  fi

  for package in "${packages[@]}"; do
    if ! apt_package_is_installed "$package"; then
      missing+=("$package")
    fi
  done

  if ! has_dotnet_10_sdk; then
    if apt-cache show dotnet-sdk-10.0 >/dev/null 2>&1; then
      missing+=(dotnet-sdk-10.0)
    else
      die "dotnet SDK 10.0 is required but dotnet-sdk-10.0 is not available from configured apt repositories. Install .NET 10 SDK or configure the Microsoft package feed, then rerun."
    fi
  fi

  if [[ "${#missing[@]}" -eq 0 ]]; then
    return
  fi

  echo "Installing missing build prerequisites with apt-get."
  run_with_sudo apt-get update
  run_with_sudo apt-get install -y --no-install-recommends "${missing[@]}"
}

install_build_dependencies_with_dnf() {
  local packages=(bash coreutils diffutils findutils desktop-file-utils ca-certificates)
  local missing=()
  local package

  if wants_deb; then
    packages+=(dpkg)
  fi
  if wants_rpm; then
    packages+=(rpm-build cpio)
  fi
  if ! has_dotnet_10_sdk; then
    packages+=(dotnet-sdk-10.0)
  fi

  for package in "${packages[@]}"; do
    if ! rpm_package_is_installed "$package"; then
      missing+=("$package")
    fi
  done

  if [[ "${#missing[@]}" -eq 0 ]]; then
    return
  fi

  echo "Installing missing build prerequisites with dnf."
  run_with_sudo dnf install -y "${missing[@]}"
}

install_build_dependencies() {
  if command -v apt-get >/dev/null 2>&1; then
    install_build_dependencies_with_apt
  elif command -v dnf >/dev/null 2>&1; then
    install_build_dependencies_with_dnf
  else
    die "apt-get or dnf is required to install build prerequisites; use --skip-build-deps after installing them manually"
  fi
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "$1 is required"
}

write_control_file() {
  local target="$1"
  local saw_version=0
  local line

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == Version:* ]]; then
      echo "Version: $PACKAGE_VERSION"
      saw_version=1
    else
      echo "$line"
    fi
  done < "$CONTROL_SOURCE" > "$target"

  [[ "$saw_version" -eq 1 ]] || die "control file does not contain a Version field"
}

install_debian_maintainer_script() {
  local source="$1"
  local target="$2"

  # Windows and shared-folder checkouts can expose CRLF despite .gitattributes.
  sed 's/\r$//' "$source" > "$target"
  chmod 0755 "$target"
  [[ "$(head -n 1 "$target")" == "#!/bin/sh" ]] \
    || die "invalid maintainer-script shebang: $source"
  if LC_ALL=C grep -q "$(printf '\r')" "$target"; then
    die "maintainer script still contains CR characters: $source"
  fi
}

write_build_identity() {
  local target="$1"
  local build_id
  local revision="unknown"
  local dirty="false"

  build_id="$(date -u +%Y%m%dT%H%M%SZ)"
  if command -v git >/dev/null 2>&1 \
      && git -C "$ROOT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    revision="$(git -C "$ROOT_DIR" rev-parse HEAD 2>/dev/null || echo unknown)"
    if [[ -n "$(git -C "$ROOT_DIR" status --porcelain --untracked-files=normal 2>/dev/null)" ]]; then
      dirty="true"
    fi
  fi

  printf '{\n  "version": "%s",\n  "buildId": "%s",\n  "gitRevision": "%s",\n  "dirty": %s\n}\n' \
    "$PACKAGE_VERSION" "$build_id" "$revision" "$dirty" > "$target"
}

validate_build_identity() {
  local target="$1"
  [[ -f "$target" ]] || die "build identity is missing: $target"
  grep -Fq "\"version\": \"$PACKAGE_VERSION\"" "$target" \
    || die "build identity version does not match $PACKAGE_VERSION"
  if [[ "$ALLOW_DIRTY_SOURCE" != "1" ]]; then
    grep -Fq '"dirty": false' "$target" \
      || die "release and smoke-test package identity must report dirty=false"
  fi

  if command -v git >/dev/null 2>&1 \
      && git -C "$ROOT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    local expected_revision
    expected_revision="$(git -C "$ROOT_DIR" rev-parse HEAD 2>/dev/null || echo unknown)"
    [[ "$expected_revision" != "unknown" ]] \
      || die "could not resolve the source revision for build identity"
    grep -Fq "\"gitRevision\": \"$expected_revision\"" "$target" \
      || die "build identity revision does not match $expected_revision"
  fi
}

validate_clean_installed_identity() {
  local target="$1"
  grep -Fq '"dirty": false' "$target" \
    || die "installed smoke-test package identity does not report dirty=false: $target"
}

stage_payload() {
  echo "Staging normalized package payload at $PAYLOAD_ROOT."
  rm -rf -- "$PAYLOAD_ROOT"
  mkdir -p "$PAYLOAD_ROOT/opt/libreguard-vpn-linux"
  mkdir -p "$PAYLOAD_ROOT/usr/libexec/libreguard-vpn-linux"
  mkdir -p "$PAYLOAD_ROOT/usr/lib/NetworkManager/dispatcher.d/pre-up.d"
  mkdir -p "$PAYLOAD_ROOT/etc/NetworkManager/dispatcher.d/pre-up.d"
  mkdir -p "$PAYLOAD_ROOT/usr/share/applications"
  mkdir -p "$PAYLOAD_ROOT/usr/share/icons/hicolor/256x256/apps"
  mkdir -p "$PAYLOAD_ROOT/usr/share/polkit-1/actions"

  cp -a "$PUBLISH_DIR/." "$PAYLOAD_ROOT/opt/libreguard-vpn-linux/"
  install -m 0755 \
    "$ROOT_DIR/packaging/linux/helpers/libreguard-ikev2-route-repair" \
    "$PAYLOAD_ROOT/usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair"
  install -m 0755 \
    "$ROOT_DIR/packaging/linux/helpers/libreguard-ipv6-leak-protection" \
    "$PAYLOAD_ROOT/usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection"
  install -m 0755 \
    "$ROOT_DIR/packaging/linux/dispatcher/90-libreguard-vpn-lifecycle" \
    "$PAYLOAD_ROOT/usr/lib/NetworkManager/dispatcher.d/90-libreguard-vpn-lifecycle"
  install -m 0755 \
    "$ROOT_DIR/packaging/linux/dispatcher/90-libreguard-vpn-lifecycle" \
    "$PAYLOAD_ROOT/usr/lib/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle"
  install -m 0755 \
    "$ROOT_DIR/packaging/linux/dispatcher/90-libreguard-vpn-lifecycle" \
    "$PAYLOAD_ROOT/etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle"
  install -m 0644 \
    "$ROOT_DIR/packaging/linux/polkit/net.libreguard.vpn.linux.repair-ikev2-routing.policy" \
    "$PAYLOAD_ROOT/usr/share/polkit-1/actions/net.libreguard.vpn.linux.repair-ikev2-routing.policy"
  install -m 0644 \
    "$ROOT_DIR/packaging/linux/libreguard-vpn-linux.desktop" \
    "$PAYLOAD_ROOT/usr/share/applications/libreguard-vpn-linux.desktop"
  install -m 0644 \
    "$PACKAGE_ICON_SOURCE" \
    "$PAYLOAD_ROOT/usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png"

  chmod -R u=rwX,go=rX "$PAYLOAD_ROOT/opt/libreguard-vpn-linux"
  chmod 0755 "$PAYLOAD_ROOT/opt/libreguard-vpn-linux/libreguard-vpn-linux"
  find "$PAYLOAD_ROOT/opt/libreguard-vpn-linux" -type f -name "*.sh" -exec chmod 0755 {} +
  find "$PAYLOAD_ROOT/opt/libreguard-vpn-linux" -type f -name "*.so" -exec chmod 0755 {} +
  find "$PAYLOAD_ROOT" -type d -exec chmod 0755 {} +
}

compare_payload() {
  local extracted_root="$1"
  diff -qr "$PAYLOAD_ROOT" "$extracted_root" >/dev/null \
    || die "packaged payload does not match the normalized publish payload"
}

validate_deb_package() {
  local package_path="$1"
  local package_contents
  local extracted_root="$BUILD_ROOT/deb-validate"

  echo "Inspecting $package_path"
  dpkg-deb --info "$package_path"
  dpkg-deb --field "$package_path" Depends | grep -q "libwebkit2gtk-4.1-0"
  dpkg-deb --field "$package_path" Depends | grep -q "fonts-noto-color-emoji"
  dpkg-deb --field "$package_path" Depends | grep -q "gnome-keyring"
  dpkg-deb --field "$package_path" Depends | grep -q "libpam-gnome-keyring"
  dpkg-deb --field "$package_path" Depends | grep -q "policykit-1"
  dpkg-deb --field "$package_path" Depends | grep -q "pkexec"
  dpkg-deb --field "$package_path" Recommends | grep -q "libwpewebkit-2.0-1"
  dpkg-deb --field "$package_path" Depends | grep -q "xdg-utils"
  package_contents="$(dpkg-deb --contents "$package_path")"
  echo "$package_contents"

  grep -q "./opt/libreguard-vpn-linux/libreguard-vpn-linux" <<< "$package_contents"
  grep -q "./opt/libreguard-vpn-linux/build-info.json" <<< "$package_contents"
  grep -q "./usr/share/applications/libreguard-vpn-linux.desktop" <<< "$package_contents"
  grep -q "./usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png" <<< "$package_contents"
  grep -Eq "^drwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./opt/libreguard-vpn-linux/$" <<< "$package_contents"
  grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./opt/libreguard-vpn-linux/libhostfxr\\.so$" <<< "$package_contents"
  grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./opt/libreguard-vpn-linux/libreguard-vpn-linux$" <<< "$package_contents"
  grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair$" <<< "$package_contents"
  grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection$" <<< "$package_contents"
  grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./usr/lib/NetworkManager/dispatcher\\.d/90-libreguard-vpn-lifecycle$" <<< "$package_contents"
  grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./usr/lib/NetworkManager/dispatcher\\.d/pre-up\\.d/90-libreguard-vpn-lifecycle$" <<< "$package_contents"
  grep -Eq "^-rwxr-xr-x[[:space:]]+root/root[[:space:]].*\\./etc/NetworkManager/dispatcher\\.d/pre-up\\.d/90-libreguard-vpn-lifecycle$" <<< "$package_contents"
  grep -Eq "^-rw-r--r--[[:space:]]+root/root[[:space:]].*\\./usr/share/polkit-1/actions/net\\.libreguard\\.vpn\\.linux\\.repair-ikev2-routing\\.policy$" <<< "$package_contents"

  rm -rf -- "$extracted_root"
  mkdir -p "$extracted_root"
  dpkg-deb -x "$package_path" "$extracted_root"
  compare_payload "$extracted_root"
}

validate_rpm_package() {
  local package_path="$1"
  local package_contents
  local package_requirements
  local package_metadata
  local extracted_root="$BUILD_ROOT/rpm-validate"
  local dependency

  echo "Inspecting $package_path"
  rpm -qpi "$package_path"
  [[ "$(rpm -qp --qf '%{VERSION}' "$package_path")" == "$PACKAGE_VERSION" ]] \
    || die "RPM version does not match $PACKAGE_VERSION"
  [[ "$(rpm -qp --qf '%{RELEASE}' "$package_path")" == "$RPM_PACKAGE_RELEASE" ]] \
    || die "RPM release does not match $RPM_PACKAGE_RELEASE"
  [[ "$(rpm -qp --qf '%{ARCH}' "$package_path")" == "x86_64" ]] \
    || die "RPM architecture is not x86_64"

  package_requirements="$(rpm -qpR "$package_path")"
  echo "$package_requirements"
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
    grep -Fxq "$dependency" <<< "$package_requirements" \
      || die "RPM is missing dependency: $dependency"
  done

  package_contents="$(rpm -qpl "$package_path")"
  echo "$package_contents"
  grep -Fxq "/opt/libreguard-vpn-linux/libreguard-vpn-linux" <<< "$package_contents"
  grep -Fxq "/opt/libreguard-vpn-linux/build-info.json" <<< "$package_contents"
  grep -Fxq "/usr/share/applications/libreguard-vpn-linux.desktop" <<< "$package_contents"
  grep -Fxq "/usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png" <<< "$package_contents"
  grep -Fxq "/usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair" <<< "$package_contents"
  grep -Fxq "/usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection" <<< "$package_contents"
  grep -Fxq "/usr/lib/NetworkManager/dispatcher.d/90-libreguard-vpn-lifecycle" <<< "$package_contents"
  grep -Fxq "/usr/lib/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle" <<< "$package_contents"
  grep -Fxq "/etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle" <<< "$package_contents"
  grep -Fxq "/usr/share/polkit-1/actions/net.libreguard.vpn.linux.repair-ikev2-routing.policy" <<< "$package_contents"
  grep -Fxq "/usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil" <<< "$package_contents"

  package_metadata="$(rpm -qp --qf '[%{FILENAMES}\t%{FILEUSERNAME}\t%{FILEGROUPNAME}\t%{FILEMODES:perms}\n]' "$package_path")"
  echo "$package_metadata"
  grep -Eq "^/opt/libreguard-vpn-linux/libreguard-vpn-linux[[:space:]]+root[[:space:]]+root[[:space:]]+-rwxr-xr-x$" <<< "$package_metadata"
  grep -Eq "^/usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair[[:space:]]+root[[:space:]]+root[[:space:]]+-rwxr-xr-x$" <<< "$package_metadata"
  grep -Eq "^/usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection[[:space:]]+root[[:space:]]+root[[:space:]]+-rwxr-xr-x$" <<< "$package_metadata"
  grep -Eq "^/usr/share/polkit-1/actions/net\\.libreguard\\.vpn\\.linux\\.repair-ikev2-routing\\.policy[[:space:]]+root[[:space:]]+root[[:space:]]+-rw-r--r--$" <<< "$package_metadata"
  grep -Eq "^/usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora\\.cil[[:space:]]+root[[:space:]]+root[[:space:]]+-rw-r--r--$" <<< "$package_metadata"

  rm -rf -- "$extracted_root"
  mkdir -p "$extracted_root"
  (
    cd "$extracted_root"
    rpm2cpio "$package_path" | cpio -idm --quiet
  )
  diff -q \
    "$ROOT_DIR/packaging/linux/selinux/libreguard_ikev2_fedora.cil" \
    "$extracted_root/usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil" >/dev/null \
    || die "RPM Fedora IKEv2 SELinux policy does not match its source"
  rm -f -- "$extracted_root/usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil"
  rmdir --ignore-fail-on-non-empty \
    "$extracted_root/usr/share/selinux/packages/libreguard" \
    "$extracted_root/usr/share/selinux/packages" \
    "$extracted_root/usr/share/selinux"
  compare_payload "$extracted_root"
}

validate_desktop_file() {
  if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$ROOT_DIR/packaging/linux/libreguard-vpn-linux.desktop"
  else
    echo "desktop-file-validate not found; skipping desktop file validation."
  fi
}

build_deb_package() {
  echo "Building Debian package."
  rm -rf -- "$PACKAGE_ROOT"
  mkdir -p "$PACKAGE_ROOT/DEBIAN"
  cp -a "$PAYLOAD_ROOT/." "$PACKAGE_ROOT/"
  write_control_file "$PACKAGE_ROOT/DEBIAN/control"
  install_debian_maintainer_script \
    "$ROOT_DIR/packaging/linux/deb/postinst" \
    "$PACKAGE_ROOT/DEBIAN/postinst"
  install_debian_maintainer_script \
    "$ROOT_DIR/packaging/linux/deb/postrm" \
    "$PACKAGE_ROOT/DEBIAN/postrm"
  install_debian_maintainer_script \
    "$ROOT_DIR/packaging/linux/deb/prerm" \
    "$PACKAGE_ROOT/DEBIAN/prerm"
  chmod 0755 "$PACKAGE_ROOT" "$PACKAGE_ROOT/DEBIAN"

  mkdir -p "$OUTPUT_DIR"
  rm -f -- "$DEB_PATH"
  dpkg-deb --root-owner-group --build "$PACKAGE_ROOT" "$DEB_PATH"
  validate_deb_package "$DEB_PATH"
  echo "Package written to $DEB_PATH"
}

build_rpm_package() {
  local built_rpm="$RPM_TOPDIR/RPMS/x86_64/libreguard-vpn-linux-${PACKAGE_VERSION}-${RPM_PACKAGE_RELEASE}.x86_64.rpm"

  echo "Building RPM package."
  mkdir -p \
    "$RPM_TOPDIR/BUILD" \
    "$RPM_TOPDIR/BUILDROOT" \
    "$RPM_TOPDIR/RPMS" \
    "$RPM_TOPDIR/SOURCES" \
    "$RPM_TOPDIR/SPECS" \
    "$RPM_TOPDIR/SRPMS"
  rpmbuild -bb "$RPM_SPEC_SOURCE" \
    --define "_topdir $RPM_TOPDIR" \
    --define "package_version $PACKAGE_VERSION" \
    --define "package_release $RPM_PACKAGE_RELEASE" \
    --define "payload_root $PAYLOAD_ROOT" \
    --define "selinux_policy_source $ROOT_DIR/packaging/linux/selinux/libreguard_ikev2_fedora.cil"

  [[ -f "$built_rpm" ]] || die "rpmbuild did not produce the expected package: $built_rpm"
  mkdir -p "$OUTPUT_DIR"
  rm -f -- "$RPM_PATH"
  cp -a "$built_rpm" "$RPM_PATH"
  validate_rpm_package "$RPM_PATH"
  echo "Package written to $RPM_PATH"
}

ensure_app_is_not_running() {
  if command -v pgrep >/dev/null 2>&1 \
      && pgrep -f '^/opt/libreguard-vpn-linux/libreguard-vpn-linux([[:space:]]|$)' >/dev/null 2>&1; then
    die "LibreGuard is running. Close it before installing this package."
  fi
}

compare_installed_file() {
  local expected="$1"
  local installed="$2"
  [[ -f "$installed" ]] || die "installed file is missing: $installed"
  [[ "$(sha256sum "$expected" | awk '{print $1}')" == "$(sha256sum "$installed" | awk '{print $1}')" ]] \
    || die "installed file does not match the built package: $installed"
}

install_and_verify_deb() {
  local verify_root="$BUILD_ROOT/deb-install-verify"
  local installed_version

  command -v apt-get >/dev/null 2>&1 \
    || die "apt-get is required to install and verify a Debian package"
  ensure_app_is_not_running

  echo "Installing $DEB_PATH with dpkg."
  if ! run_with_sudo dpkg -i "$DEB_PATH"; then
    echo "Repairing package state and dependencies with the local archive."
    run_with_sudo apt-get install -y --no-install-recommends --fix-broken "$DEB_PATH" \
      || die "package recovery failed; the local archive remains at $DEB_PATH"
    run_with_sudo dpkg -i "$DEB_PATH"
  fi

  apt_package_is_installed libreguard-vpn-linux \
    || die "dpkg did not report libreguard-vpn-linux as installed"
  installed_version="$(dpkg-query -W -f='${Version}' libreguard-vpn-linux 2>/dev/null)"
  [[ "$installed_version" == "$PACKAGE_VERSION" ]] \
    || die "installed version $installed_version does not match $PACKAGE_VERSION"

  rm -rf -- "$verify_root"
  mkdir -p "$verify_root"
  dpkg-deb -x "$DEB_PATH" "$verify_root"
  compare_installed_file \
    "$verify_root/opt/libreguard-vpn-linux/libreguard-vpn-linux" \
    "/opt/libreguard-vpn-linux/libreguard-vpn-linux"
  compare_installed_file \
    "$verify_root/opt/libreguard-vpn-linux/build-info.json" \
    "/opt/libreguard-vpn-linux/build-info.json"
  validate_clean_installed_identity "/opt/libreguard-vpn-linux/build-info.json"
  echo "Installed LibreGuard $installed_version and verified Debian package identity."
}

install_and_verify_rpm() {
  local verify_root="$BUILD_ROOT/rpm-install-verify"
  local installed_version

  command -v dnf >/dev/null 2>&1 \
    || die "dnf is required to install and verify an RPM package"
  ensure_app_is_not_running

  echo "Installing $RPM_PATH with dnf."
  run_with_sudo dnf install -y "$RPM_PATH"
  rpm_package_is_installed libreguard-vpn-linux \
    || die "rpm did not report libreguard-vpn-linux as installed"
  installed_version="$(rpm -q --qf '%{VERSION}' libreguard-vpn-linux)"
  [[ "$installed_version" == "$PACKAGE_VERSION" ]] \
    || die "installed version $installed_version does not match $PACKAGE_VERSION"

  rm -rf -- "$verify_root"
  mkdir -p "$verify_root"
  (
    cd "$verify_root"
    rpm2cpio "$RPM_PATH" | cpio -idm --quiet
  )
  compare_installed_file \
    "$verify_root/opt/libreguard-vpn-linux/libreguard-vpn-linux" \
    "/opt/libreguard-vpn-linux/libreguard-vpn-linux"
  compare_installed_file \
    "$verify_root/opt/libreguard-vpn-linux/build-info.json" \
    "/opt/libreguard-vpn-linux/build-info.json"
  validate_clean_installed_identity "/opt/libreguard-vpn-linux/build-info.json"
  echo "Installed LibreGuard $installed_version and verified RPM package identity."
}

if [[ "$SKIP_BUILD_DEPS" -eq 0 ]]; then
  install_build_dependencies
else
  echo "Skipping build dependency installation."
fi

require_command dotnet
require_command install
require_command sha256sum
require_command diff
  if ! has_dotnet_10_sdk; then
    die ".NET SDK 10.0 is required to build this project"
  fi
  if wants_deb; then
    require_command dpkg-deb
    if ! dpkg_deb_supports_root_owner_group; then
    die "dpkg-deb does not support --root-owner-group; install dpkg 1.19.0 or newer."
  fi
fi
if wants_rpm; then
  require_command rpmbuild
  require_command rpm
  require_command rpm2cpio
  require_command cpio
fi

publish_args=()
publish_args+=("/p:AppVersion=Linux/$PACKAGE_VERSION")
if [[ "$ALLOW_MISSING_GOOGLE_OAUTH_CLIENT_ID" == "1" ]]; then
  publish_args+=(/p:AllowMissingGoogleOAuthClientId=true)
fi

validate_published_app_version() {
  local settings_path="$PUBLISH_DIR/appsettings.json"
  [[ -f "$settings_path" ]] || die "published appsettings.json is missing"
  grep -Fq "\"AppVersion\": \"Linux/$PACKAGE_VERSION\"" "$settings_path" \
    || die "published app version does not match Linux/$PACKAGE_VERSION"
}

echo "Publishing LibreGuard VPN Linux for linux-x64."
EXPECTED_PUBLISH_DIR="$ROOT_DIR/bin/Release/net10.0/linux-x64/publish"
[[ "$PUBLISH_DIR" == "$EXPECTED_PUBLISH_DIR" ]] \
  || die "refusing to clear unexpected publish directory: $PUBLISH_DIR"
rm -rf -- "$PUBLISH_DIR"
dotnet publish "$ROOT_DIR/libreguard-vpn-linux.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  --artifacts-path "$BUILD_ROOT" \
  --output "$PUBLISH_DIR" \
  --force \
  --disable-build-servers \
  "${publish_args[@]}"

validate_published_app_version
write_build_identity "$PUBLISH_DIR/build-info.json"
validate_build_identity "$PUBLISH_DIR/build-info.json"
stage_payload
validate_desktop_file

if wants_deb; then
  build_deb_package
fi
if wants_rpm; then
  build_rpm_package
fi

if [[ "$INSTALL_PACKAGE" -eq 1 ]]; then
  case "$PACKAGE_FORMAT" in
    deb) install_and_verify_deb ;;
    rpm) install_and_verify_rpm ;;
  esac
fi
