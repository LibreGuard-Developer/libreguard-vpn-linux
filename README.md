# LibreGuard VPN Linux

LibreGuard VPN Linux is an open-source Linux client for the LibreGuard VPN service. It provides a desktop interface with support for IKEv2 and OpenVPN® connections.

## Features

- Linux desktop client built with .NET 10 and Avalonia.
- IKEv2 and OpenVPN® connection support.
- Google sign-in and account management.
- DNS filtering and kill-switch controls.
- Secure local storage for sessions and device credentials.
- Debian and RPM packages for supported 64-bit Linux distributions.

## Linux support

LibreGuard VPN Linux currently supports Ubuntu 24.04 or later and Fedora 43/44 on 64-bit x86 systems. ARM64, Fedora Atomic/rpm-ostree, and RHEL-based distributions are not current release targets.

## Install LibreGuard

When public builds are available, download the latest package from [GitHub Releases](../../releases).

Install on Ubuntu or Debian:

```bash
sudo apt-get install ./libreguard-vpn-linux_<version>_amd64.deb
```

Install on Fedora:

```bash
sudo dnf install ./libreguard-vpn-linux-<version>-<release>.x86_64.rpm
```

## Build from source

Build on Linux with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Git, and the dependencies required by your distribution.

```bash
git clone <repository-url>
cd libreguard-vpn-linux

dotnet restore ./libreguard-vpn-linux.slnx
dotnet build ./libreguard-vpn-linux.slnx --configuration Release
dotnet test ./libreguard-vpn-linux.slnx --configuration Release
```

Publish a self-contained Linux build:

```bash
dotnet publish ./libreguard-vpn-linux.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true
```

Release builds may require a public Google OAuth client ID supplied through the build environment.

## Contributing

Issues and pull requests are welcome. Please describe problems and proposed changes clearly, keep pull requests focused, and include relevant tests. For substantial changes, open an issue first so the community can discuss the approach.

## Security

Please report security issues privately according to [SECURITY.md](SECURITY.md).
