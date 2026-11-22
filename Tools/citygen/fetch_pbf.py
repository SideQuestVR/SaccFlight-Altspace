"""Stage 0: download the regional OpenStreetMap extract used by extract.py.

Data (c) OpenStreetMap contributors, ODbL. One ~90 MB file; re-running skips it if present.
"""
import sys

import requests

import config as C

URL = "https://download.openstreetmap.fr/extracts/asia/united_arab_emirates-latest.osm.pbf"


def main() -> None:
    dst = C.CACHE / "uae-latest.osm.pbf"
    if dst.exists() and "--force" not in sys.argv:
        print(f"already have {dst} ({dst.stat().st_size / 1e6:.0f} MB); use --force to refresh")
        return
    C.CACHE.mkdir(parents=True, exist_ok=True)
    tmp = dst.with_suffix(".part")
    with requests.get(URL, stream=True, timeout=120, headers={"User-Agent": "citygen-osm-fetch/1.0"}) as resp:
        resp.raise_for_status()
        with open(tmp, "wb") as fh:
            for chunk in resp.iter_content(1 << 20):
                fh.write(chunk)
    tmp.replace(dst)
    print(f"downloaded {dst} ({dst.stat().st_size / 1e6:.0f} MB)")


if __name__ == "__main__":
    main()
