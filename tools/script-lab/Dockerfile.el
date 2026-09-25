# A throwaway CentOS-family "VPS" for testing install scripts: systemd, sshd, sudo and room for its own Docker.
# Built for quay.io/centos/centos:stream9 and rockylinux/rockylinux:9 (see compose.yml). curl stays curl-minimal,
# as on the cloud images. SELinux and firewalld cannot run in a container: check those on a real server.
ARG BASE=quay.io/centos/centos:stream9
FROM ${BASE}
ENV container=docker
RUN dnf -y install systemd openssh-server sudo ca-certificates iproute procps-ng kmod passwd hostname less nano \
 && dnf clean all \
 # units that make no sense in a container
 && systemctl mask systemd-udevd.service systemd-udev-trigger.service systemd-modules-load.service sys-kernel-debug.mount sys-kernel-tracing.mount getty.target console-getty.service \
 && systemctl enable sshd \
 && sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin yes/; s/^#\?PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config \
 && echo 'root:lab-root' | chpasswd \
 # a sudo user with a password (wheel), like on most VPS images
 && useradd -m -s /bin/bash -G wheel lab && echo 'lab:lab-user' | chpasswd
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
