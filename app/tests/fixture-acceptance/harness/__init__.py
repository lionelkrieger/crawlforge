"""WP-08-04 fixture acceptance harness package.

Measures the real CrawlForge product against app/tests/fixture-site through
normal entry points only (UI over CDP, REST, MCP, schedule run-now). Never
modifies product code, the fixture, or expectations; never patches state.
Python 3.14 stdlib only; Node helpers use built-ins + the workspace-hoisted
MCP SDK.
"""

HARNESS_VERSION = "1.0.0"
WORKPIECE = "WP-08-04"