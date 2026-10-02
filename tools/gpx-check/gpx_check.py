#!/usr/bin/env python3
"""
gpx-check : valide (et nettoie) une trace GPX enregistrée pour la tâche H-001 (cahier des charges §20.4).

    python3 tools/gpx-check/gpx_check.py scenarios
    python3 tools/gpx-check/gpx_check.py check  brute.gpx --scenario walk [--home 43.2,5.3] [--json]
    python3 tools/gpx-check/gpx_check.py sanitize brute.gpx --scenario walk -o data-pipeline/gpx/real_walk_vieux_port.gpx
    python3 tools/gpx-check/gpx_check.py near data-pipeline/gpx/real_walk_vieux_port.gpx --candidates data-pipeline/gpx/candidates.json --radius 150
    python3 tools/gpx-check/gpx_check.py candidates --pois data-pipeline/marseille/pois.json -o data-pipeline/gpx/real_walk_vieux_port.candidates.json

`sanitize` retire tout ce qui est personnel (métadonnées, noms, e-mails, points de passage, extensions d'appareil), coupe les 100 premiers
et les 100 derniers mètres (domicile, hôtel) et décale les dates ; `check` refuse une trace qui ne respecte pas le scénario ou qui n'est
pas propre. Aucune dépendance hors de la bibliothèque standard Python 3.9+. Code de sortie : 0 = conforme, 1 = erreurs, 2 = usage.
"""
from __future__ import annotations

import argparse
import json
import math
import re
import statistics
import sys
import uuid
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

TRIM_METERS = 100.0
SANITIZED_CREATOR = "ON.VOYAGE gpx-check sanitized"
REFERENCE_START = datetime(2026, 6, 14, 9, 0, 0, tzinfo=timezone.utc)
GAP_SECONDS = 20.0  # same threshold as the replay harness (Replay.cs): a longer silence is a "gap"
SPEED_WINDOW_SECONDS = 10.0
MAX_ACCEPTABLE_ACCURACY_M = 30.0


@dataclass(frozen=True)
class Scenario:
    key: str
    label: str
    min_minutes: float
    max_minutes: float
    min_km: float
    median_kmh: tuple[float, float]
    max_kmh: float
    jump_kmh: float          # implied speed between two fixes above which the fix is counted as a "jump"
    max_median_interval_s: float
    min_points: int
    gap: str                 # "none" | "required"
    jitter: str              # "clean" | "required"
    suggested: str


SCENARIOS: dict[str, Scenario] = {s.key: s for s in [
    Scenario("walk", "Marche 3 km/h", 15, 120, 1.0, (2.0, 6.5), 12, 20, 5, 200, "none", "clean",
             "Vieux-Port, Fort Saint-Jean, Le Panier"),
    Scenario("bike", "Vélo 20 km/h", 8, 90, 2.0, (12.0, 30.0), 55, 70, 5, 150, "none", "clean",
             "Corniche Kennedy, du Vallon des Auffes à la plage du Prophète"),
    Scenario("car50", "Voiture 50 km/h", 5, 90, 4.0, (30.0, 70.0), 95, 160, 4, 150, "none", "clean",
             "Route des Crêtes (D141), en passager"),
    Scenario("highway110", "Autoroute 110 km/h", 5, 90, 8.0, (85.0, 130.0), 145, 220, 3, 100, "none", "clean",
             "A50, en passager"),
    Scenario("tunnel", "Perte GPS (tunnel)", 5, 90, 1.0, (10.0, 70.0), 100, 160, 5, 100, "required", "clean",
             "Tunnel routier ou passage couvert long, en passager ou à vélo"),
    Scenario("canyon", "GPS imprécis (canyon urbain)", 5, 90, 0.3, (0.5, 60.0), 400, 20, 6, 100, "none", "required",
             "Rues étroites et hautes : Le Panier, rue Caisserie"),
]}


@dataclass
class Fix:
    lat: float
    lon: float
    t: datetime
    accuracy: float | None = None


@dataclass
class Report:
    errors: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    info: list[str] = field(default_factory=list)
    metrics: dict[str, object] = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return not self.errors


def haversine_m(a: Fix | tuple[float, float], b: Fix | tuple[float, float]) -> float:
    lat1, lon1 = (a.lat, a.lon) if isinstance(a, Fix) else a
    lat2, lon2 = (b.lat, b.lon) if isinstance(b, Fix) else b
    p1, p2 = math.radians(lat1), math.radians(lat2)
    d_lat, d_lon = p2 - p1, math.radians(lon2 - lon1)
    h = math.sin(d_lat / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(d_lon / 2) ** 2
    return 2 * 6_371_000.0 * math.asin(min(1.0, math.sqrt(h)))


# ---------------------------------------------------------------------------------------------------------------------- reading

def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_time(text: str) -> datetime:
    value = text.strip().replace("Z", "+00:00")
    parsed = datetime.fromisoformat(value)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def read_gpx(path: Path) -> tuple[ET.Element, list[Fix], list[str]]:
    """Returns the XML root, the fixes of every track segment in file order and the problems met while reading."""
    problems: list[str] = []
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as error:
        raise ValueError(f"Fichier GPX illisible : {error}") from error
    if _local(root.tag) != "gpx":
        raise ValueError("Ce fichier n'est pas un GPX (élément racine différent de <gpx>).")
    fixes: list[Fix] = []
    for point in root.iter():
        if _local(point.tag) != "trkpt":
            continue
        try:
            lat, lon = float(point.attrib["lat"]), float(point.attrib["lon"])
        except (KeyError, ValueError):
            problems.append("Un point <trkpt> n'a pas de lat/lon valides.")
            continue
        if not (-90 <= lat <= 90 and -180 <= lon <= 180):
            problems.append(f"Coordonnées hors limites : {lat}, {lon}.")
            continue
        time_text = next((child.text for child in point if _local(child.tag) == "time" and child.text), None)
        if time_text is None:
            problems.append("Un point n'a pas d'horodatage <time> : la trace ne peut pas être rejouée.")
            continue
        try:
            t = parse_time(time_text)
        except ValueError:
            problems.append(f"Horodatage illisible : {time_text!r}.")
            continue
        fixes.append(Fix(lat, lon, t, _accuracy(point)))
    return root, fixes, problems


def _accuracy(point: ET.Element) -> float | None:
    for node in point.iter():
        name = _local(node.tag).lower()
        if node.text and name in ("accuracy", "hacc", "horizontalaccuracy"):
            try:
                return float(node.text)
            except ValueError:
                return None
    for node in point.iter():
        if node.text and _local(node.tag).lower() == "hdop":  # rough conversion: 1 HDOP ~ 5 m
            try:
                return float(node.text) * 5.0
            except ValueError:
                return None
    return None


# ---------------------------------------------------------------------------------------------------------------------- privacy

EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
PHONE = re.compile(r"(?<!\d)(?:\+?\d[\d .-]{8,}\d)(?!\d)")
UUID = re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
LONG_DIGITS = re.compile(r"\b\d{14,16}\b")  # IMEI, serial numbers
PERSONAL_ELEMENTS = {"name", "desc", "cmt", "author", "email", "link", "keywords", "copyright", "src", "text"}
PERSONAL_CONTAINERS = {"wpt", "rte"}
ALLOWED_EXTENSION_TAGS = {"accuracy", "hdop"}


def privacy_findings(root: ET.Element, synthetic: bool) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    warnings: list[str] = []
    creator = root.attrib.get("creator", "")
    for needle, label in ((EMAIL, "une adresse e-mail"), (UUID, "un identifiant (UUID)"), (LONG_DIGITS, "un numéro long (série, IMEI)")):
        if needle.search(creator):
            errors.append(f"L'attribut creator contient {label}.")
    if not synthetic and creator and creator != SANITIZED_CREATOR:
        warnings.append(f"creator={creator!r} : la trace n'a pas encore été nettoyée par `sanitize` (coupe de {TRIM_METERS:.0f} m aux deux bouts non vérifiable).")
    if not synthetic and not creator:
        warnings.append("Pas d'attribut creator : la trace n'a pas encore été nettoyée par `sanitize`.")

    for node in root.iter():
        name = _local(node.tag).lower()
        if name in PERSONAL_CONTAINERS and not synthetic:
            errors.append(f"Élément <{name}> (point de passage ou itinéraire) : à supprimer.")
        if name in PERSONAL_ELEMENTS and not synthetic and _has_content(node):
            errors.append(f"Élément <{name}> renseigné ({_preview(node)}) : métadonnée possiblement personnelle, à supprimer.")
        text = " ".join(filter(None, [node.text, *node.attrib.values()]))
        if not text.strip():
            continue
        if EMAIL.search(text):
            errors.append(f"Adresse e-mail trouvée dans <{name}>.")
        elif UUID.search(text) or LONG_DIGITS.search(text):
            errors.append(f"Identifiant d'appareil ou numéro de série probable dans <{name}>.")
        elif PHONE.search(text) and name not in ("time", "trkpt"):
            warnings.append(f"Suite de chiffres ressemblant à un numéro de téléphone dans <{name}>.")

    extension_tags = {_local(n.tag).lower() for ext in root.iter() if _local(ext.tag) == "extensions" for n in ext.iter() if n is not ext}
    foreign = sorted(tag for tag in extension_tags if tag not in ALLOWED_EXTENSION_TAGS)
    if foreign and not synthetic:
        warnings.append(f"Extensions d'appareil ou d'application ({', '.join(foreign[:6])}) : `sanitize` les retire.")
    return errors, warnings


def _has_content(node: ET.Element) -> bool:
    return bool((node.text or "").strip()) or bool(node.attrib) or any(True for _ in node)


def _preview(node: ET.Element) -> str:
    text = (node.text or "").strip() or ",".join(f"{k}=…" for k in node.attrib)
    return (text[:20] + "…") if len(text) > 20 else (text or "contenu")


# ---------------------------------------------------------------------------------------------------------------------- metrics

def windowed_speeds_kmh(fixes: list[Fix]) -> list[float]:
    """Speed over windows of ~10 s: robust to the noise of a single fix. Windows crossing a gap are skipped."""
    speeds: list[float] = []
    j = 0
    for i, first in enumerate(fixes):
        j = max(j, i + 1)
        while j < len(fixes) and (fixes[j].t - first.t).total_seconds() < SPEED_WINDOW_SECONDS:
            j += 1
        if j >= len(fixes):
            break
        dt = (fixes[j].t - first.t).total_seconds()
        if dt > SPEED_WINDOW_SECONDS * 3:
            continue
        speeds.append(haversine_m(first, fixes[j]) / dt * 3.6)
    return speeds


def compute_metrics(fixes: list[Fix], scenario: Scenario) -> dict[str, object]:
    intervals = [(b.t - a.t).total_seconds() for a, b in zip(fixes, fixes[1:])]
    gaps = [(round((a.t - fixes[0].t).total_seconds()), round((b.t - fixes[0].t).total_seconds()))
            for a, b in zip(fixes, fixes[1:]) if (b.t - a.t).total_seconds() > GAP_SECONDS]
    distance = sum(haversine_m(a, b) for a, b in zip(fixes, fixes[1:]) if (b.t - a.t).total_seconds() <= GAP_SECONDS)
    speeds = windowed_speeds_kmh(fixes)
    moving = [s for s in speeds if s >= 0.5]
    jumps = 0
    for a, b in zip(fixes, fixes[1:]):
        dt = (b.t - a.t).total_seconds()
        if 0 < dt <= GAP_SECONDS and haversine_m(a, b) / dt * 3.6 > scenario.jump_kmh:
            jumps += 1
    poor = sum(1 for f in fixes if f.accuracy is not None and f.accuracy > MAX_ACCEPTABLE_ACCURACY_M)
    jittery = {i for i, f in enumerate(fixes) if f.accuracy is not None and f.accuracy > MAX_ACCEPTABLE_ACCURACY_M}
    for i, (a, b) in enumerate(zip(fixes, fixes[1:]), start=1):
        dt = (b.t - a.t).total_seconds()
        if 0 < dt <= GAP_SECONDS and haversine_m(a, b) / dt * 3.6 > scenario.jump_kmh:
            jittery.add(i)
    return {
        "points": len(fixes),
        "duration_min": round((fixes[-1].t - fixes[0].t).total_seconds() / 60, 1),
        "distance_km": round(distance / 1000, 2),
        "median_interval_s": round(statistics.median(intervals), 2) if intervals else None,
        "median_moving_kmh": round(statistics.median(moving), 1) if moving else 0.0,
        "p95_kmh": round(sorted(moving)[int(0.95 * (len(moving) - 1))], 1) if moving else 0.0,
        "gaps": gaps,
        "jumps": jumps,
        "poor_accuracy_fixes": poor,
        "has_accuracy": any(f.accuracy is not None for f in fixes),
        "jitter_ratio": round(len(jittery) / len(fixes), 3),
    }


# ---------------------------------------------------------------------------------------------------------------------- check

def check_trace(root: ET.Element, fixes: list[Fix], read_problems: list[str], scenario: Scenario, home: tuple[float, float] | None = None) -> Report:
    report = Report()
    report.errors.extend(read_problems)
    synthetic = "synthetic" in root.attrib.get("creator", "").lower()
    if synthetic:
        report.info.append("Trace synthétique (creator contient 'synthetic') : contrôles de confidentialité allégés.")

    if len(fixes) < 2:
        report.errors.append("Moins de deux points exploitables.")
        return report

    order_errors = sum(1 for a, b in zip(fixes, fixes[1:]) if b.t <= a.t)
    if order_errors:
        report.errors.append(f"{order_errors} point(s) dont l'horodatage ne croît pas strictement : la trace est désordonnée ou dupliquée.")
        fixes = sorted({f.t: f for f in fixes}.values(), key=lambda f: f.t)

    m = compute_metrics(fixes, scenario)
    report.metrics.update(m)

    if len(fixes) < scenario.min_points:
        report.errors.append(f"{len(fixes)} points : il en faut au moins {scenario.min_points} pour « {scenario.label} ».")
    duration = float(m["duration_min"])
    if not scenario.min_minutes <= duration <= scenario.max_minutes:
        report.errors.append(f"Durée de {duration} min : attendu entre {scenario.min_minutes:g} et {scenario.max_minutes:g} min.")
    if float(m["distance_km"]) < scenario.min_km:
        report.errors.append(f"Distance de {m['distance_km']} km : au moins {scenario.min_km:g} km attendus.")

    low, high = scenario.median_kmh
    median = float(m["median_moving_kmh"])
    if not low <= median <= high:
        report.errors.append(f"Vitesse médiane en mouvement de {median} km/h : attendu entre {low:g} et {high:g} km/h pour « {scenario.label} ».")
    if float(m["p95_kmh"]) > scenario.max_kmh and scenario.key != "canyon":
        report.errors.append(f"Vitesse (95e centile) de {m['p95_kmh']} km/h au-dessus de {scenario.max_kmh:g} km/h : mauvais mode de déplacement ou trace polluée.")

    interval = m["median_interval_s"]
    if interval is None or float(interval) > scenario.max_median_interval_s:
        report.errors.append(f"Un point toutes les {interval} s en médiane : réglez l'enregistrement à 1 point par seconde (maximum {scenario.max_median_interval_s:g} s).")
    elif float(interval) < 0.5:
        report.warnings.append(f"Un point toutes les {interval} s : plus fin que nécessaire (le fichier sera lourd).")

    gaps: list[tuple[int, int]] = m["gaps"]  # type: ignore[assignment]
    if scenario.gap == "none":
        if gaps:
            report.errors.append(f"{len(gaps)} trou(s) de plus de {GAP_SECONDS:g} s (premier à {gaps[0][0]} s) : ce scénario doit rester continu. Vérifiez l'économiseur de batterie.")
    else:
        usable = [g for g in gaps if 45 <= g[1] - g[0] <= 900]
        if not usable:
            report.errors.append("Aucune perte de signal de 45 s à 15 min : le scénario « tunnel » exige un vrai trou dans la trace (ne l'éditez pas à la main).")
        elif len(usable) < len(gaps):
            report.warnings.append("Des trous hors de la plage 45 s – 15 min existent aussi : vérifiez qu'ils sont voulus.")

    jitter = float(m["jitter_ratio"])
    if scenario.jitter == "required":
        if jitter < 0.2:
            report.errors.append(f"Seulement {jitter:.0%} de points imprécis ou en saut : ce scénario en attend au moins 20 %. Choisissez des rues plus étroites et hautes.")
    elif jitter > 0.1:
        report.errors.append(f"{jitter:.0%} de points imprécis (> {MAX_ACCEPTABLE_ACCURACY_M:g} m) ou en saut : trace trop bruitée pour ce scénario (au plus 10 %). Refaites-la en ciel dégagé.")
    if not m["has_accuracy"]:
        report.warnings.append("Aucune précision (accuracy/hdop) dans la trace : le rejeu supposera 10 m. Utilisez une application qui l'enregistre.")

    errors, warnings = privacy_findings(root, synthetic)
    report.errors.extend(errors)
    report.warnings.extend(warnings)

    if home is not None:
        for label, fix in (("début", fixes[0]), ("fin", fixes[-1])):
            distance = haversine_m(fix, home)
            if distance < TRIM_METERS:
                report.errors.append(f"Le {label} de la trace est à {distance:.0f} m du point --home : coupez {TRIM_METERS:.0f} m (commande `sanitize`).")
    elif not synthetic:
        report.warnings.append("Passez --home LAT,LON (domicile, hôtel) pour vérifier la coupe des 100 premiers et derniers mètres ; la valeur n'est ni écrite ni conservée.")

    return report


# ---------------------------------------------------------------------------------------------------------------------- sanitize

def trim_ends(fixes: list[Fix], meters: float = TRIM_METERS) -> list[Fix]:
    """Drops the points covering the first and the last `meters` of travelled distance."""
    if len(fixes) < 3:
        return []
    start, total = 0, 0.0
    while start + 1 < len(fixes) and total < meters:
        total += haversine_m(fixes[start], fixes[start + 1])
        start += 1
    end, total = len(fixes) - 1, 0.0
    while end - 1 > start and total < meters:
        total += haversine_m(fixes[end], fixes[end - 1])
        end -= 1
    return fixes[start:end + 1]


def sanitized_gpx(fixes: list[Fix], keep_dates: bool = False) -> str:
    shift = timedelta(0) if keep_dates else REFERENCE_START - fixes[0].t
    lines = ['<?xml version="1.0" encoding="UTF-8"?>',
             f'<gpx version="1.1" creator="{SANITIZED_CREATOR}" xmlns="http://www.topografix.com/GPX/1/1">', "  <trk><trkseg>"]
    for fix in fixes:
        stamp = (fix.t + shift).astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        extension = f"<extensions><accuracy>{fix.accuracy:.0f}</accuracy></extensions>" if fix.accuracy is not None else ""
        lines.append(f'    <trkpt lat="{fix.lat:.6f}" lon="{fix.lon:.6f}"><time>{stamp}</time>{extension}</trkpt>')
    lines += ["  </trkseg></trk>", "</gpx>", ""]
    return "\n".join(lines)


# ---------------------------------------------------------------------------------------------------------------------- candidates

CANDIDATES_NAMESPACE = uuid.uuid5(uuid.NAMESPACE_DNS, "on.voyage/field-gpx")
WORDS_PER_SECOND = 2.5  # ~150 words per minute of narration


def candidates_from_snapshot(pois: list[dict]) -> list[dict]:
    """Candidate places for a replay, from the Marseille snapshot (data-pipeline/marseille/pois.json).
    Identifiers are derived from the slug (replay only). baseScore, visibleFromRoad and carAccessible are not in the snapshot: defaults that you
    may edit by hand in the generated file when a scenario depends on them (car)."""
    result = []
    for poi in pois:
        words = len(str(poi.get("story", {}).get("text", "")).split())
        result.append({
            "poiId": str(uuid.uuid5(CANDIDATES_NAMESPACE, poi["slug"])), "name": poi["name"], "latitude": poi["latitude"], "longitude": poi["longitude"],
            "importance": poi["importance"], "baseScore": 0.7, "crowdLevel": int(poi.get("crowd", {}).get("shoulder", 2)), "fragile": bool(poi.get("fragile", False)),
            "visibleFromRoad": False, "carAccessible": False, "storyId": str(uuid.uuid5(CANDIDATES_NAMESPACE, "story/" + poi["slug"])),
            "storySeconds": max(30, round(words / WORDS_PER_SECOND)) if words else 90,
        })
    return result


def places_near(fixes: list[Fix], places: list[dict], radius_m: float) -> list[tuple[str, float, float]]:
    """(name, closest distance in metres, seconds since the start at the closest fix) of every candidate within `radius_m` of the trace."""
    found = []
    for place in places:
        point = (place["latitude"], place["longitude"])
        distance, at = min(((haversine_m(fix, point), (fix.t - fixes[0].t).total_seconds()) for fix in fixes), key=lambda pair: pair[0])
        if distance <= radius_m:
            found.append((place["name"], round(distance), round(at)))
    return sorted(found, key=lambda item: item[2])


# ---------------------------------------------------------------------------------------------------------------------- CLI

def _home(value: str | None) -> tuple[float, float] | None:
    if value is None:
        return None
    try:
        lat, lon = (float(part) for part in value.split(","))
    except ValueError as error:
        raise argparse.ArgumentTypeError("--home attend LAT,LON (ex. 43.2965,5.3698).") from error
    return lat, lon


def print_report(report: Report, name: str, scenario: Scenario, as_json: bool) -> None:
    if as_json:
        print(json.dumps({"file": name, "scenario": scenario.key, "ok": report.ok, "errors": report.errors, "warnings": report.warnings,
                          "info": report.info, "metrics": report.metrics}, ensure_ascii=False, indent=2))
        return
    print(f"{name} - scénario « {scenario.label} »")
    for key, value in report.metrics.items():
        print(f"  {key}: {value}")
    for item in report.info:
        print(f"  [info] {item}")
    for item in report.warnings:
        print(f"  [avertissement] {item}")
    for item in report.errors:
        print(f"  [ERREUR] {item}")
    print("  => CONFORME" if report.ok else f"  => NON CONFORME ({len(report.errors)} erreur(s))")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="gpx-check", description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("scenarios", help="liste les scénarios et leurs seuils")
    near = sub.add_parser("near", help="liste les lieux candidats passés à moins de --radius mètres (aide à écrire expected-places)")
    near.add_argument("gpx", type=Path)
    near.add_argument("--candidates", type=Path, default=Path("data-pipeline/gpx/candidates.json"))
    near.add_argument("--radius", type=float, default=150.0)
    cand = sub.add_parser("candidates", help="produit un fichier de lieux candidats depuis le snapshot Marseille")
    cand.add_argument("--pois", type=Path, default=Path("data-pipeline/marseille/pois.json"))
    cand.add_argument("-o", "--output", type=Path, required=True)
    for name in ("check", "sanitize"):
        p = sub.add_parser(name)
        p.add_argument("gpx", type=Path)
        p.add_argument("--scenario", required=True, choices=sorted(SCENARIOS))
        p.add_argument("--home", type=_home, help="LAT,LON du domicile/hôtel (comparaison seulement, jamais écrit)")
        p.add_argument("--json", action="store_true")
        p.add_argument("--strict", action="store_true", help="les avertissements deviennent des erreurs")
        if name == "sanitize":
            p.add_argument("-o", "--output", type=Path, required=True)
            p.add_argument("--keep-dates", action="store_true", help="garde les dates réelles (par défaut : décalées au 2026-06-14)")
    args = parser.parse_args(argv)

    if args.command == "scenarios":
        for s in SCENARIOS.values():
            print(f"{s.key:11} {s.label:32} durée {s.min_minutes:g}-{s.max_minutes:g} min, ≥ {s.min_km:g} km, médiane {s.median_kmh[0]:g}-{s.median_kmh[1]:g} km/h, "
                  f"trous: {s.gap}, bruit: {s.jitter}  ({s.suggested})")
        return 0

    if args.command == "near":
        try:
            _, fixes, _ = read_gpx(args.gpx)
            places = json.loads(args.candidates.read_text(encoding="utf-8"))
        except (OSError, ValueError) as error:
            print(f"ERREUR : {error}", file=sys.stderr)
            return 1
        for name, distance, at in places_near(fixes, places, args.radius):
            print(f"t={at:>6} s  à {distance:>4} m  {name}")
        return 0

    if args.command == "candidates":
        try:
            pois = json.loads(args.pois.read_text(encoding="utf-8"))
            args.output.write_text(json.dumps(candidates_from_snapshot(pois), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        except (OSError, ValueError, KeyError) as error:
            print(f"ERREUR : {error}", file=sys.stderr)
            return 1
        print(f"{len(pois)} lieu(x) écrit(s) dans {args.output}")
        return 0

    scenario = SCENARIOS[args.scenario]
    try:
        root, fixes, problems = read_gpx(args.gpx)
    except ValueError as error:
        print(f"ERREUR : {error}", file=sys.stderr)
        return 1

    if args.command == "sanitize":
        trimmed = trim_ends(fixes)
        if not trimmed:
            print("ERREUR : trace trop courte pour couper 100 m à chaque bout.", file=sys.stderr)
            return 1
        args.output.write_text(sanitized_gpx(trimmed, args.keep_dates), encoding="utf-8")
        print(f"{len(fixes) - len(trimmed)} point(s) retiré(s) aux extrémités ; fichier propre écrit : {args.output}")
        root, fixes, problems = read_gpx(args.output)

    report = check_trace(root, fixes, problems, scenario, args.home)
    if args.strict:
        report.errors.extend(report.warnings)
        report.warnings = []
    print_report(report, args.gpx.name if args.command == "check" else args.output.name, scenario, args.json)
    return 0 if report.ok else 1


if __name__ == "__main__":
    sys.exit(main())
