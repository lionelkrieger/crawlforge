# Criterion #3 — idle & reporting, zero outbound

Configuration: fresh install — no destinations, no external database, no schedule due.

- Idle phase (5s): non-loopback observations = 0
- Reporting phase (9 read-only routes): non-loopback observations = 0
- Total observed connections: 55
- Total non-loopback: **0** (required: 0)

Raw observations: idle-reporting-connections.csv