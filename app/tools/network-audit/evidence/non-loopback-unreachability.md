# Criterion #22/#16 — non-loopback unreachability

Backend port 58125 probed via every non-loopback IPv4
interface of this machine:

- 172.18.64.1:58125 → unreachable
- 192.168.8.106:58125 → unreachable

Interfaces probed: 2 (0 means the machine has no
non-loopback IPv4 interface; the listener sweep still proves loopback-only binds).