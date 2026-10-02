"""Tests de gpx_check.py : python3 -m unittest discover -s tools/gpx-check -v"""
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from datetime import datetime, timedelta, timezone
from pathlib import Path

import gpx_check as g

ROOT = Path(__file__).resolve().parents[2]
START = datetime(2026, 3, 2, 14, 0, 0, tzinfo=timezone.utc)
METERS_PER_DEGREE = 111_320.0


def build(metres_per_second=1.3, seconds=1200, step=1, creator="Strava", head="", skip=(), accuracy=8, extension="", noise=None, start=START):
    """A straight walk heading north from the Vieux-Port, one point every `step` seconds."""
    lines = [f'<?xml version="1.0"?><gpx version="1.1" creator="{creator}" xmlns="http://www.topografix.com/GPX/1/1">{head}<trk><trkseg>']
    for t in range(0, seconds, step):
        if skip and skip[0] < t < skip[1]:
            continue
        lat = 43.2950 + metres_per_second * t / METERS_PER_DEGREE
        lon = 5.3745
        bad = bool(noise and noise(t))
        if bad:
            lat += 120 / METERS_PER_DEGREE
        stamp = (start + timedelta(seconds=t)).strftime("%Y-%m-%dT%H:%M:%SZ")
        lines.append(f'<trkpt lat="{lat:.6f}" lon="{lon:.6f}"><time>{stamp}</time><extensions><accuracy>{80 if bad else accuracy}</accuracy>{extension}</extensions></trkpt>')
    lines.append("</trkseg></trk></gpx>")
    return "\n".join(lines)


def run(xml, scenario, home=None):
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "t.gpx"
        path.write_text(xml, encoding="utf-8")
        root, fixes, problems = g.read_gpx(path)
        return g.check_trace(root, fixes, problems, g.SCENARIOS[scenario], home)


class Scenarios(unittest.TestCase):
    def test_a_clean_walk_matches_the_walk_scenario_apart_from_cleaning(self):
        report = run(build(), "walk")
        self.assertEqual([], report.errors)
        self.assertTrue(any("sanitize" in w for w in report.warnings))

    def test_a_walk_is_not_a_bike_ride(self):
        report = run(build(), "bike")
        self.assertTrue(any("Vitesse médiane" in e for e in report.errors))

    def test_car_speed_is_refused_as_a_walk(self):
        report = run(build(metres_per_second=14, seconds=900), "walk")
        self.assertTrue(any("Vitesse" in e for e in report.errors))

    def test_a_short_trace_is_refused(self):
        report = run(build(seconds=120), "walk")
        self.assertTrue(any("Durée" in e for e in report.errors))

    def test_a_gap_breaks_every_scenario_but_the_tunnel(self):
        xml = build(metres_per_second=5, seconds=900, skip=(300, 400))
        self.assertTrue(any("trou" in e for e in run(xml, "bike").errors))
        self.assertEqual([], [e for e in run(xml, "tunnel").errors if "trou" in e or "perte" in e.lower()])

    def test_the_tunnel_scenario_needs_a_real_gap(self):
        self.assertTrue(any("Aucune perte" in e for e in run(build(metres_per_second=5, seconds=900), "tunnel").errors))

    def test_the_canyon_needs_bad_fixes_and_the_others_refuse_them(self):
        noisy = build(metres_per_second=1.2, seconds=900, noise=lambda t: t % 3 == 0)
        self.assertEqual([], run(noisy, "canyon").errors)
        self.assertTrue(any("imprécis" in e for e in run(noisy, "walk").errors))
        self.assertTrue(any("au moins 20 %" in e for e in run(build(seconds=900), "canyon").errors))

    def test_sparse_recording_is_refused(self):
        self.assertTrue(any("1 point par seconde" in e for e in run(build(step=10, seconds=3000), "walk").errors))

    def test_disordered_timestamps_are_refused(self):
        lines = build(seconds=600).split("\n")
        lines[5], lines[6] = lines[6], lines[5]
        self.assertTrue(any("horodatage" in e for e in run("\n".join(lines), "walk").errors))

    def test_the_synthetic_traces_of_the_repository_pass(self):
        mapping = {"walk_vieux_port": "walk", "bike_corniche": "bike", "car_route_des_cretes": "car50", "car_a50_highway_110kmh": "highway110",
                   "tunnel_loss": "tunnel", "gps_jitter_urban_canyon": "canyon"}
        for name, scenario in mapping.items():
            root, fixes, problems = g.read_gpx(ROOT / "data-pipeline" / "gpx" / f"{name}.gpx")
            report = g.check_trace(root, fixes, problems, g.SCENARIOS[scenario])
            self.assertEqual([], report.errors, name)


class Privacy(unittest.TestCase):
    def test_personal_metadata_is_an_error(self):
        head = "<metadata><name>Footing de Jean Dupont</name><author><name>Jean</name><email id='jean' domain='example.org'/></author></metadata>"
        errors = run(build(head=head), "walk").errors
        self.assertTrue(any("<name>" in e for e in errors))
        self.assertTrue(any("<author>" in e for e in errors))

    def test_an_email_anywhere_is_an_error(self):
        errors = run(build(head="<metadata><keywords>jean.dupont@example.org</keywords></metadata>"), "walk").errors
        self.assertTrue(any("e-mail" in e for e in errors))

    def test_device_identifiers_are_an_error(self):
        errors = run(build(creator="Phone 3f2a8c1e-9b7d-4c1a-8e55-0a1b2c3d4e5f"), "walk").errors
        self.assertTrue(any("UUID" in e for e in errors))
        errors = run(build(head="<metadata><desc>IMEI 356938035643809</desc></metadata>"), "walk").errors
        self.assertTrue(any("série" in e or "IMEI" in e for e in errors))

    def test_waypoints_are_an_error(self):
        self.assertTrue(any("wpt" in e for e in run(build(head='<wpt lat="43.3" lon="5.4"/>'), "walk").errors))

    def test_foreign_extensions_are_flagged_for_removal(self):
        self.assertTrue(any("Extensions" in w for w in run(build(extension="<deviceModel>X</deviceModel>"), "walk").warnings))

    def test_home_within_100_m_of_the_ends_is_an_error(self):
        self.assertTrue(any("--home" in e for e in run(build(), "walk", (43.2950, 5.3745)).errors))
        self.assertEqual([], [e for e in run(build(), "walk", (43.3100, 5.3745)).errors if "--home" in e])


class Sanitize(unittest.TestCase):
    def test_sanitize_strips_trims_100_m_shifts_dates_and_passes_the_check(self):
        dirty = build(head="<metadata><name>Jean</name><author><name>Jean</name></author></metadata>", extension="<deviceModel>Pixel</deviceModel>")
        with tempfile.TemporaryDirectory() as folder:
            source, target = Path(folder) / "dirty.gpx", Path(folder) / "clean.gpx"
            source.write_text(dirty, encoding="utf-8")
            with redirect_stdout(io.StringIO()):
                code = g.main(["sanitize", str(source), "--scenario", "walk", "-o", str(target), "--home", "43.2950,5.3745"])
            self.assertEqual(0, code)
            text = target.read_text(encoding="utf-8")
            for forbidden in ("Jean", "Pixel", "metadata", "<name>", "2026-03-02"):
                self.assertNotIn(forbidden, text)
            root, fixes, problems = g.read_gpx(target)
            original = g.read_gpx(source)[1]
            self.assertLess(len(fixes), len(original))
            self.assertGreaterEqual(g.haversine_m(fixes[0], original[0]), 100)
            self.assertGreaterEqual(g.haversine_m(fixes[-1], original[-1]), 100)
            self.assertEqual(g.REFERENCE_START, fixes[0].t)
            report = g.check_trace(root, fixes, problems, g.SCENARIOS["walk"], (43.2950, 5.3745))
            self.assertEqual([], report.errors)
            self.assertEqual([], [w for w in report.warnings if "sanitize" in w])

    def test_keep_dates(self):
        with tempfile.TemporaryDirectory() as folder:
            source, target = Path(folder) / "a.gpx", Path(folder) / "b.gpx"
            source.write_text(build(), encoding="utf-8")
            with redirect_stdout(io.StringIO()):
                g.main(["sanitize", str(source), "--scenario", "walk", "-o", str(target), "--keep-dates"])
            self.assertIn("2026-03-02", target.read_text(encoding="utf-8"))


class Candidates(unittest.TestCase):
    def test_candidates_come_from_the_snapshot_in_the_replay_format(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "c.json"
            with redirect_stdout(io.StringIO()):
                self.assertEqual(0, g.main(["candidates", "--pois", str(ROOT / "data-pipeline" / "marseille" / "pois.json"), "-o", str(target)]))
            places = json.loads(target.read_text(encoding="utf-8"))
            self.assertGreater(len(places), 20)
            expected_keys = set(json.loads((ROOT / "data-pipeline" / "gpx" / "candidates.json").read_text(encoding="utf-8"))[0])
            self.assertEqual(expected_keys, set(places[0]))
            self.assertEqual(len(places), len({p["poiId"] for p in places}))


class Near(unittest.TestCase):
    def test_lists_the_places_passed_in_order_of_time(self):
        root, fixes, _ = g.read_gpx(ROOT / "data-pipeline" / "gpx" / "walk_vieux_port.gpx")
        places = json.loads((ROOT / "data-pipeline" / "gpx" / "candidates.json").read_text(encoding="utf-8"))
        found = g.places_near(fixes, places, 100)
        names = [name for name, _, _ in found]
        self.assertIn("Hôtel de Ville", names)
        self.assertNotIn("Plage du Prophète", names)
        self.assertEqual(sorted(t for _, _, t in found), [t for _, _, t in found])


class Cli(unittest.TestCase):
    def test_exit_codes_and_json(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "t.gpx"
            path.write_text(build(), encoding="utf-8")
            out = io.StringIO()
            with redirect_stdout(out):
                self.assertEqual(0, g.main(["check", str(path), "--scenario", "walk", "--json"]))
            self.assertTrue(json.loads(out.getvalue())["ok"])
            with redirect_stdout(io.StringIO()):
                self.assertEqual(1, g.main(["check", str(path), "--scenario", "bike"]))
                self.assertEqual(1, g.main(["check", str(path), "--scenario", "walk", "--strict"]))

    def test_an_unreadable_file_is_reported_without_a_traceback(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "t.gpx"
            path.write_text("pas du xml", encoding="utf-8")
            self.assertEqual(1, g.main(["check", str(path), "--scenario", "walk"]))


if __name__ == "__main__":
    unittest.main()
