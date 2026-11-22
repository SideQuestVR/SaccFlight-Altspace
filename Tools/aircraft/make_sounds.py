"""Synthesise the aircraft sounds (no recordings, no third-party audio): seamless loops for a jet engine,
a propeller engine and wind, and a one-shot explosion. Written as 16-bit mono WAV to Assets/Flight/Audio.

    Tools/citygen/.venv/Scripts/python.exe Tools/aircraft/make_sounds.py

Loops are built in the frequency domain (every component has a whole number of cycles in the loop), so the
last sample runs into the first with no click. The graph changes each loop's pitch and volume at run time,
so the loops are recorded at a "cruise" setting and kept free of anything that would give a pitch change away
(no slow sweeps, no beats slower than the loop).
"""
from __future__ import annotations

import wave
from pathlib import Path

import numpy as np

RATE = 32000
OUT = Path(__file__).resolve().parents[2] / "Assets" / "Flight" / "Audio"
rng = np.random.default_rng(20261121)


def shaped_noise(seconds: float, shape) -> np.ndarray:
    """Periodic noise whose spectrum follows shape(frequency in Hz): random phases, inverse FFT."""
    n = int(RATE * seconds)
    freqs = np.fft.rfftfreq(n, 1.0 / RATE)
    spectrum = shape(freqs) * np.exp(1j * rng.uniform(0, 2 * np.pi, freqs.size))
    spectrum[0] = 0
    return np.fft.irfft(spectrum, n)


def tone(seconds: float, hz: float, phase: float = 0.0) -> np.ndarray:
    """A sine with a whole number of cycles in the loop."""
    n = int(RATE * seconds)
    cycles = max(1, round(hz * seconds))
    return np.sin(2 * np.pi * cycles * np.arange(n) / n + phase)


def band(freqs, centre, width):
    return np.exp(-0.5 * ((freqs - centre) / width) ** 2)


def normalise(x: np.ndarray, peak: float = 0.85) -> np.ndarray:
    return x / np.max(np.abs(x)) * peak


def save(name: str, samples: np.ndarray) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    data = (np.clip(samples, -1, 1) * 32767).astype("<i2")
    with wave.open(str(OUT / name), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(data.tobytes())
    rms = float(np.sqrt(np.mean(samples ** 2)))
    seam = abs(float(samples[0] - samples[-1]))
    step = float(np.max(np.abs(np.diff(samples))))
    print(f"{name}: {len(samples) / RATE:.2f} s, peak {np.max(np.abs(samples)):.2f}, rms {rms:.3f}, seam jump {seam:.4f} (largest ordinary step {step:.4f})")


def jet_loop(seconds: float = 3.0) -> np.ndarray:
    # exhaust roar: noise falling off above a few hundred Hz, with a body resonance; plus compressor whine
    roar = shaped_noise(seconds, lambda f: 1.0 / (1.0 + (f / 280.0) ** 1.6) + 0.5 * band(f, 620, 240))
    hiss = shaped_noise(seconds, lambda f: band(f, 4200, 1800)) * 0.22
    whine = 0.10 * tone(seconds, 2350) + 0.06 * tone(seconds, 4700, 1.1) + 0.035 * tone(seconds, 3525, 0.4)
    flutter = 1.0 + 0.10 * shaped_noise(seconds, lambda f: band(f, 14, 8))       # turbulence in the exhaust
    return normalise((normalise(roar, 1.0) * flutter + hiss) * 0.9 + whine)


def prop_loop(seconds: float = 3.0) -> np.ndarray:
    # piston engine and propeller: a firing fundamental with harmonics, blade beat, and some air noise
    base = 78.0
    harmonics = [(1, 1.0), (2, 0.72), (3, 0.46), (4, 0.34), (5, 0.20), (6, 0.15), (8, 0.09), (10, 0.05)]
    drone = sum(a * tone(seconds, base * k, rng.uniform(0, 6.28)) for k, a in harmonics)
    rough = 1.0 + 0.22 * tone(seconds, base / 2, 0.7) + 0.08 * shaped_noise(seconds, lambda f: band(f, 30, 14))
    air = shaped_noise(seconds, lambda f: band(f, 1500, 900)) * 0.16
    return normalise(drone * rough / 2.2 + air)


def wind_loop(seconds: float = 4.0) -> np.ndarray:
    rush = shaped_noise(seconds, lambda f: band(f, 900, 700) + 0.6 / (1.0 + (f / 180.0) ** 2))
    gust = 1.0 + 0.25 * shaped_noise(seconds, lambda f: band(f, 1.2, 0.8))
    return normalise(normalise(rush, 1.0) * np.clip(gust, 0.5, 1.6), 0.8)


def explosion(seconds: float = 2.6) -> np.ndarray:
    n = int(RATE * seconds)
    t = np.arange(n) / RATE
    thump = np.sin(2 * np.pi * (58 * t - 14 * t ** 2).clip(0)) * np.exp(-t * 3.2)             # falling low thud
    crack = rng.standard_normal(n) * np.exp(-t * 26.0)                                        # the initial snap
    rumble = shaped_noise(seconds, lambda f: 1.0 / (1.0 + (f / 160.0) ** 2)) * np.exp(-t * 1.9)
    debris = shaped_noise(seconds, lambda f: band(f, 2600, 1500)) * np.exp(-((t - 0.25) / 0.35) ** 2) * 0.25
    x = 0.9 * thump + 0.7 * crack + 0.9 * normalise(rumble, 1.0) + debris
    x *= np.minimum(1.0, t / 0.004)                                                            # 4 ms attack, no click
    x *= np.minimum(1.0, (seconds - t) / 0.25)                                                 # fade the tail out
    return normalise(np.tanh(x * 1.6), 0.95)


if __name__ == "__main__":
    save("jet_loop.wav", jet_loop())
    save("prop_loop.wav", prop_loop())
    save("wind_loop.wav", wind_loop())
    save("explosion.wav", explosion())
