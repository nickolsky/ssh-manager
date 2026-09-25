# A throwaway "VPS" for testing install scripts: systemd, sshd, sudo and room for its own Docker.
# Built for ubuntu:24.04, ubuntu:22.04, debian:12, debian:13 (see compose.yml).
ARG BASE=ubuntu:24.04
FROM ${BASE}
ENV DEBIAN_FRONTEND=noninteractive container=docker
RUN apt-get update \
 && apt-get install -y --no-install-recommends systemd systemd-sysv dbus openssh-server sudo ca-certificates curl iproute2 iptables kmod procps less nano \
 && apt-get clean && rm -rf /var/lib/apt/lists/* \
 # units that make no sense in a container
 && rm -f /lib/systemd/system/multi-user.target.wants/getty.target /etc/systemd/system/getty.target.wants/* \
 && systemctl mask systemd-udevd.service systemd-udev-trigger.service systemd-modules-load.service sys-kernel-debug.mount sys-kernel-tracing.mount getty.target console-getty.service \
 && systemctl enable ssh \
 # base images block service starts during apt installs; a real server does not
 && rm -f /usr/sbin/policy-rc.d \
 && mkdir -p /run/sshd \
 && sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin yes/; s/^#\?PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config \
 && echo 'root:lab-root' | chpasswd \
 # a sudo user with a password, like on most VPS images
 && useradd -m -s /bin/bash -G sudo lab && echo 'lab:lab-user' | chpasswd
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
