# Criterion #20 — metrics boundary

- GET /metrics: 200, text/plain Prometheus-class exposition (642 chars)
- Ambient identity values absent (user/machine/domain): yes
- Identity label names absent: yes (machine_guid, machineguid, hostname, user_name, username, os_user, device_id, installation_id)
- Non-loopback connections during idle + scrape (3s): **0** (required: 0 — pull-only, never pushed)
- Local log files scanned: 0; identity leaks: **0** (required: 0)