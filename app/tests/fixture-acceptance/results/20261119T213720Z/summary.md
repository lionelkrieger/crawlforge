# Fixture acceptance — criterion #56 UI-edit proof (20261119T213720Z)

- harness 1.0.0, git HEAD `a8efa6dbf5d0dc9230e7c67dc69ab2e4a65500ad`
- expected-results.json sha256 `9068684d683e7f4dcedabef4223d35ed69421e49ed4af591e353288a2d780211`
- #56 UI-edit proof: run A (jar off, maxUrls 50 via UI-04) audit 41 fetched=50, cookie-gate loop=True; run B (jar on via UI-04) audit 42 cookie-gate=pass; all 17 checks OK

| check | ok | detail |
|---|---|---|
| a-jar-off-via-ui | OK | editor switch {'before': True, 'after': False, 'method': 'input (input)'} |
| a-maxurls-50-via-ui | OK | editor limit field {'steps': ['unlimited-off:input (input)', 'fill-override:50'], 'override_value': '50', 'unlimited_after': False} |
| a-save-readback | OK | UI save PUT + REST read-back {'put_seen': True, 'read_back': {'cookieJar.enabled': False, 'limits.maxUrls': 50, 'limits.maxDurationSeconds': None}} |
| a-audit-completed | OK | state=completed |
| a-maxurls-honored | OK | counters.fetched=50 (cap 50 set in UI-04) |
| a-cookie-gate-not-200 | OK | cookie-gate row statusCode=302 |
| a-cookie-gate-loop-finding | OK | redirects.loop finding for cookie-gate: {"id": "43181", "ruleKey": "redirects.loop", "severity": "error", "category": "indexability", "urlId": "6961", "message" |
| a-cookie-gate-verdict-not-pass | OK | verdict=partial (jar off must not pass) |
| b-jar-on-via-ui | OK | editor switch {'before': False, 'after': True, 'method': 'input (input)'} |
| b-maxurls-unlimited-via-ui | OK | editor limit field {'steps': ['unlimited-on:input (input)'], 'override_value': '', 'unlimited_after': True} |
| b-duration-300-via-ui | OK | editor duration field {'steps': ['unlimited-off:input (input)', 'fill-override:300'], 'override_value': '300'} |
| b-save-readback | OK | UI save PUT + REST read-back {'put_seen': True, 'read_back': {'cookieJar.enabled': True, 'limits.maxUrls': None, 'limits.maxDurationSeconds': 300}} |
| b-audit-completed | OK | state=completed |
| b-url-cap-lifted | OK | counters.fetched=613 (> run A's UI-set 50 cap proves the Unlimited flip) |
| b-cookie-gate-200 | OK | cookie-gate row statusCode=200 |
| b-cookie-gate-verdict-pass | OK | verdict=pass |
| safety-nonget-empty | OK | GET /__fixture/requests nonGet==[] after both audits |