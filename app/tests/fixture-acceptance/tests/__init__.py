"""WP-08-04 harness self-tests (stdlib unittest).

Run from the harness folder:  python -m unittest discover -s tests -v
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))