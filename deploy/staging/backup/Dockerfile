# Backup service: pg_dump / pg_basebackup client 16, age (encryption), bash. Runs as uid 999 (same as the postgres user of the postgis image,
# which owns the WAL hand-over volume). No secret in the image: the public age key and the database password come from the environment.
FROM alpine:3.21

RUN apk add --no-cache bash coreutils findutils postgresql16-client age tzdata \
    && addgroup -g 999 backup \
    && adduser -D -u 999 -G backup -s /bin/bash backup \
    && mkdir -p /backups /wal-incoming /opt/onvoyage \
    && chown backup:backup /backups /wal-incoming

COPY --chmod=0555 common.sh backup.sh backup-daemon.sh restore.sh test-restore.sh /opt/onvoyage/

USER 999:999
WORKDIR /opt/onvoyage
HEALTHCHECK --interval=60s --timeout=5s --start-period=90s --retries=3 \
    CMD find /backups/state/heartbeat -mmin -3 | grep -q . || exit 1
ENTRYPOINT ["/opt/onvoyage/backup-daemon.sh"]
