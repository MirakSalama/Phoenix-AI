"""Model location shared by the source, flat and compact Phoenix builds."""

import os
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODELS = Path(os.environ.get("PHOENIX_MODEL_DIR") or ROOT / "cache")
