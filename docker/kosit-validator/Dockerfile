# syntax=docker/dockerfile:1.7
# KoSIT validator (EN 16931 + XRechnung 3.0.2) as an HTTP service.
# Every download is pinned by version AND SHA-256; the build fails if a single byte differs.

# --- Stage 1: fetch and unpack (busybox has unzip) ---------------------------------------------
FROM busybox:1.37.0@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec AS fetch

# itplr-kosit/validator v1.6.3 (Apache-2.0)
ADD --checksum=sha256:799e64befca97d4080e03608c80b85dd5a5ecc5f4ae4f35d1116ec2855b9a7c9 \
    https://github.com/itplr-kosit/validator/releases/download/v1.6.3/validator-1.6.3-standalone.jar \
    /kosit/validator.jar

# itplr-kosit/validator-configuration-xrechnung v2026-08-31 (Apache-2.0), matches the test suite in testdata/
ADD --checksum=sha256:2530cd107c414511c5d0462ec10f886910395abfca820db82e83d70bf01221a8 \
    https://github.com/itplr-kosit/validator-configuration-xrechnung/releases/download/v2026-08-31/xrechnung-3.0.2-validator-configuration-2026-08-31.zip \
    /tmp/configuration.zip
RUN mkdir -p /kosit/configuration && unzip -q /tmp/configuration.zip -d /kosit/configuration

# --- Stage 2: runtime ------------------------------------------------------------------------------
FROM eclipse-temurin:21.0.13_9-jre-noble@sha256:0d57a8e90c2bb844c5a8e1b2b71e11eff06dad7edcfd803cd21ed4ae203b2515

COPY --from=fetch --chown=root:root --chmod=0444 /kosit/validator.jar /opt/kosit/validator.jar
COPY --from=fetch --chown=root:root /kosit/configuration /opt/kosit/configuration
# Readable (and directories enterable) by everyone, writable by root only.
RUN chmod -R a+rX,go-w /opt/kosit

# Non-root, no home directory, no shell login. Files are root-owned and read-only for this user.
USER 10001:10001
EXPOSE 8080

# -D daemon, -H 0.0.0.0 inside the container network only (the host port is bound to 127.0.0.1 in compose),
# --disable-gui: KoSIT's demo GUI is "not to be used in production environments".
ENTRYPOINT ["java", "-XX:MaxRAMPercentage=75", "-jar", "/opt/kosit/validator.jar", \
            "-s", "/opt/kosit/configuration/scenarios.xml", "-r", "/opt/kosit/configuration", \
            "-D", "-H", "0.0.0.0", "-P", "8080", "--disable-gui"]
