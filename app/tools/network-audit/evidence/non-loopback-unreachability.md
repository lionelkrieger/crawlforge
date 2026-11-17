# Criterion #22/#16 — non-loopback unreachability

Backend port 54466 probed via every non-loopback IPv4
interface of this machine:

- 172.30.64.1:54466 → unreachable
- 192.168.8.103:54466 → unreachable

Interfaces probed: 2 (0 means the machine has no
non-loopback IPv4 interface; the listener sweep still proves loopback-only binds).