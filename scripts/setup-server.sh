#!/usr/bin/env bash
# One-time setup of a fresh Ubuntu server (target: Oracle Cloud, Ubuntu 24.04, ARM64/aarch64).
# Run as the default admin user (e.g. `ubuntu`) from the repository root:
#   bash scripts/setup-server.sh
# Updates the system, installs fail2ban and automatic security updates, opens TCP 80/443 in the host
# firewall, and installs Docker Engine + Compose from Docker's official apt repository
# (https://docs.docker.com/engine/install/ubuntu/). Safe to run more than once.
# Optional: SERVER_TIMEZONE=UTC bash scripts/setup-server.sh
set -euo pipefail

# Never stop for interactive prompts (config files, service restarts).
apt_get() { sudo DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get "$@"; }

apt_get update
apt_get -y upgrade
apt_get -y install ca-certificates curl git fail2ban unattended-upgrades iptables-persistent
sudo timedatectl set-timezone "${SERVER_TIMEZONE:-America/New_York}"

# Oracle's Ubuntu images reject everything except SSH in iptables. Open 80/443 (do NOT enable ufw).
for port in 443 80; do
  sudo iptables -C INPUT -p tcp --dport "$port" -m state --state NEW -j ACCEPT 2>/dev/null ||
    sudo iptables -I INPUT 6 -p tcp --dport "$port" -m state --state NEW -j ACCEPT
done
sudo netfilter-persistent save

# Docker Engine + Compose plugin from Docker's official apt repository.
if ! command -v docker >/dev/null; then
  # Remove distribution packages that conflict with Docker's own (normally none on a fresh image).
  conflicting=$(dpkg --get-selections docker.io docker-compose docker-compose-v2 docker-doc \
    docker-buildx podman-docker containerd runc 2>/dev/null | cut -f1 || true)
  if [ -n "$conflicting" ]; then
    # shellcheck disable=SC2086
    apt_get -y remove $conflicting
  fi

  sudo install -m 0755 -d /etc/apt/keyrings
  sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  sudo chmod a+r /etc/apt/keyrings/docker.asc
  sudo tee /etc/apt/sources.list.d/docker.sources >/dev/null <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}")
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
  apt_get update
  apt_get -y install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

  # Membership of the docker group is equivalent to root access; only the administrator running
  # this script is added. Do not add other users casually.
  sudo usermod -aG docker "$USER"
  echo "Docker installed. Log out and back in so the docker group membership applies."
fi

sudo docker --version
sudo docker compose version
echo "Server setup finished."
