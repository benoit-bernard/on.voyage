#!/usr/bin/env python3
"""
Generates the SYNTHETIC GPS traces and the candidate places used by the trigger-engine replay tests (cahier des charges §14.5, §20.4).

These are not Ben's field recordings (H-001): the routes are drawn from approximate coordinates and the noise is simulated, with a fixed
seed so the output never changes. Replace the .gpx files with real traces when they exist and re-approve the expected results.

    python3 generate_synthetic.py        # rewrites candidates.json and the .gpx files next to this script
"""
import json
import math
import random
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).parent
START = datetime(2026, 6, 14, 9, 0, 0, tzinfo=timezone.utc)
NAMESPACE = uuid.uuid5(uuid.NAMESPACE_DNS, "on.voyage/synthetic-gpx")


def poi(name, lat, lon, importance, base=0.7, crowd=2, fragile=False, road=False, car=False, story_s=90):
    return {
        "poiId": str(uuid.uuid5(NAMESPACE, name)), "name": name, "latitude": lat, "longitude": lon, "importance": importance,
        "baseScore": base, "crowdLevel": crowd, "fragile": fragile, "visibleFromRoad": road, "carAccessible": car,
        "storyId": str(uuid.uuid5(NAMESPACE, "story/" + name)), "storySeconds": story_s,
    }


CANDIDATES = [
    # Vieux-Port quarter (walk)
    poi("Ombrière du Vieux-Port", 43.29508, 5.37448, 70),
    poi("Hôtel de Ville", 43.29668, 5.37005, 55),
    poi("Maison Diamantée", 43.29765, 5.36865, 52),
    poi("Vieille Charité", 43.30040, 5.36775, 65),
    poi("Cathédrale de la Major", 43.30310, 5.36495, 68),
    poi("Mucem", 43.29675, 5.36095, 72),
    poi("Fort Saint-Jean", 43.29540, 5.36060, 70),
    poi("Petit oratoire sans importance", 43.29700, 5.36950, 20),
    # Corniche (bike)
    poi("Vallon des Auffes", 43.28515, 5.35045, 62),
    poi("Monument aux morts de l'Armée d'Orient", 43.28270, 5.34925, 62),
    poi("Malmousque", 43.28100, 5.35100, 58),
    poi("Plage du Prophète", 43.26990, 5.36380, 61, crowd=4),
    # Route des Crêtes (car)
    poi("Belvédère des Crêtes (test)", 43.20935, 5.54980, 80, road=True),
    poi("Point de vue derrière (test)", 43.21550, 5.52850, 78, road=True),
    poi("Calanque fragile (test)", 43.20700, 5.55600, 85, fragile=True),
    # A50 (highway)
    poi("Repère autoroute important (test)", 43.28720, 5.48000, 72),
    poi("Repère autoroute visible (test)", 43.28900, 5.54000, 40, road=True),
    poi("Repère autoroute mineur (test)", 43.28800, 5.51000, 40),
    # Tunnel
    poi("Place dans le tunnel (test)", 43.27930, 5.38760, 70),
]


def offset(lat, lon, north_m, east_m):
    return lat + north_m / 111_320.0, lon + east_m / (111_320.0 * math.cos(math.radians(lat)))


def distance_m(a, b):
    dn = (b[0] - a[0]) * 111_320.0
    de = (b[1] - a[1]) * 111_320.0 * math.cos(math.radians(a[0]))
    return math.hypot(dn, de)


def along(waypoints, speed_mps, step_s, stops=()):
    """Positions every step_s seconds along the polyline at a constant speed; stops = [(waypoint_index, seconds)]."""
    legs = [(waypoints[i], waypoints[i + 1], distance_m(waypoints[i], waypoints[i + 1])) for i in range(len(waypoints) - 1)]
    pauses = dict(stops)
    points, t = [], 0.0
    for index, (a, b, length) in enumerate(legs):
        steps = max(1, int(length / (speed_mps * step_s)))
        for k in range(steps):
            f = k / steps
            points.append((t, (a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f)))
            t += length / steps / speed_mps
        if (index + 1) in pauses:
            end = t + pauses[index + 1]
            while t < end:
                points.append((t, b))
                t += step_s
    points.append((t, waypoints[-1]))
    return points


def noisy(points, rng, sigma_m, accuracy_m):
    out = []
    for t, (lat, lon) in points:
        lat2, lon2 = offset(lat, lon, rng.gauss(0, sigma_m), rng.gauss(0, sigma_m))
        out.append((t, lat2, lon2, accuracy_m(rng)))
    return out


def write_gpx(name, samples, description):
    lines = ['<?xml version="1.0" encoding="UTF-8"?>',
             '<gpx version="1.1" creator="ON.VOYAGE synthetic generator" xmlns="http://www.topografix.com/GPX/1/1">',
             f"  <metadata><name>{name}</name><desc>{description}</desc></metadata>", "  <trk><name>%s</name><trkseg>" % name]
    for t, lat, lon, accuracy in samples:
        time = (START + timedelta(seconds=t)).strftime("%Y-%m-%dT%H:%M:%SZ")
        lines.append(f'    <trkpt lat="{lat:.6f}" lon="{lon:.6f}"><time>{time}</time><extensions><accuracy>{accuracy:.0f}</accuracy></extensions></trkpt>')
    lines += ["  </trkseg></trk>", "</gpx>", ""]
    (HERE / f"{name}.gpx").write_text("\n".join(lines), encoding="utf-8")


def main():
    rng = random.Random(20260614)
    (HERE / "candidates.json").write_text(json.dumps(CANDIDATES, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    good = lambda r: r.uniform(6, 14)

    # 3 km/h through the old town, with a pause at the town hall.
    walk = [(43.29520, 5.37360), (43.29668, 5.37040), (43.29765, 5.36895), (43.30040, 5.36800), (43.30310, 5.36520), (43.29830, 5.36200), (43.29675, 5.36130)]
    write_gpx("walk_vieux_port", noisy(along(walk, 0.83, 3, stops=[(1, 45)]), rng, 2.5, good), "Synthetic walk at 3 km/h around the Vieux-Port, 45 s pause at the town hall.")

    # 20 km/h along the Corniche.
    bike = [(43.28890, 5.35900), (43.28600, 5.35200), (43.28515, 5.35060), (43.28270, 5.34940), (43.28100, 5.35100), (43.27400, 5.35900), (43.26990, 5.36380)]
    write_gpx("bike_corniche", noisy(along(bike, 5.5, 2, stops=()), rng, 3, good), "Synthetic ride at 20 km/h along the Corniche Kennedy.")

    # 50 km/h along the ridge road, west to east; one place sits behind the starting point.
    crests = [(43.21500, 5.53000), (43.21200, 5.54000), (43.20935, 5.54980), (43.20700, 5.55600), (43.20400, 5.57000), (43.19900, 5.59000)]
    write_gpx("car_route_des_cretes", noisy(along(crests, 13.9, 2), rng, 3, lambda r: r.uniform(8, 20)), "Synthetic drive at 50 km/h on the Route des Crêtes; a place lies behind the start.")

    # 110 km/h on a motorway.
    a50 = [(43.28600, 5.44000), (43.28720, 5.48000), (43.28800, 5.51000), (43.28900, 5.54000), (43.29000, 5.57000)]
    write_gpx("car_a50_highway_110kmh", noisy(along(a50, 30.5, 2), rng, 3, lambda r: r.uniform(5, 12)), "Synthetic drive at 110 km/h on a motorway.")

    # 20 km/h through a tunnel: a 2 minute GPS loss covers about 660 m, and the place lies in the middle of it.
    tunnel = [(43.28500, 5.38000), (43.27000, 5.40000)]
    samples = noisy(along(tunnel, 5.5, 3), rng, 2.5, good)
    gap_start, gap_end = 100.0, 220.0
    write_gpx("tunnel_loss", [s for s in samples if not gap_start < s[0] < gap_end], "Synthetic ride at 20 km/h with a 2 minute GPS loss while passing a place.")

    # Urban canyon: most fixes are poor and many jump by up to 150 m.
    street = [(43.29520, 5.37360), (43.29668, 5.37040), (43.29765, 5.36895)]
    canyon = []
    for t, (lat, lon) in along(street, 1.1, 3):
        if rng.random() < 0.6:
            lat, lon = offset(lat, lon, rng.uniform(-150, 150), rng.uniform(-150, 150))
            canyon.append((t, lat, lon, rng.uniform(70, 200)))
        else:
            lat, lon = offset(lat, lon, rng.gauss(0, 3), rng.gauss(0, 3))
            canyon.append((t, lat, lon, rng.uniform(15, 35)))
    write_gpx("gps_jitter_urban_canyon", canyon, "Synthetic walk in an urban canyon: 60 % of the fixes are poor and jump by up to 150 m.")


if __name__ == "__main__":
    main()
