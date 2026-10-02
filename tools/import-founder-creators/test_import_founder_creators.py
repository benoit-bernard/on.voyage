"""Tests de import_founder_creators.py : python3 -m unittest discover -s tools/import-founder-creators -v"""
import csv
import io
import json
import os
import re
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from datetime import date
from pathlib import Path
from unittest import mock

import import_founder_creators as imp

ROOT = Path(__file__).resolve().parents[2]
TODAY = date(2026, 10, 20)
YT = "https://www.youtube.com/watch?v=AAAAAAAAAAA"


def creator(**over):
    row = {"key": "a", "handle": "marie_test", "display_name": "Marie Test", "bio": "Bio de test.", "languages": "fr|en", "specialties": "nature.coast|history.local",
           "consent_document_ref": "FONDATEUR-TEST-001", "consent_accepted_at": "2026-09-15", "publish": "oui"}
    row.update(over)
    return row


def content(**over):
    row = {"creator_key": "a", "url": YT, "title": "Vidéo de test", "duration_seconds": "754", "is_commercial": "non", "chapters": "0:00 Intro|2:15 Vallon des Auffes"}
    row.update(over)
    return row


def link(**over):
    row = {"creator_key": "a", "place": "Vallon des Auffes", "content_url": YT, "start_time": "2:15"}
    row.update(over)
    return row


def tip(**over):
    row = {"creator_key": "a", "place": "Vallon des Auffes", "text": "Venir en fin de journée."}
    row.update(over)
    return row


def dataset(creators=None, contents=None, links=None, tips=None):
    def rows(items):
        return [imp.Row(i + 2, item) for i, item in enumerate(items or [])]
    return imp.Dataset(rows(creators if creators is not None else [creator()]), rows(contents), rows(links), rows(tips))


def errors(data):
    return [f"{i.file} {i.field}: {i.message}" for i in imp.validate(data, TODAY) if i.severity == "error"]


class Validation(unittest.TestCase):
    def test_a_complete_dataset_is_valid(self):
        self.assertEqual([], errors(dataset(contents=[content()], links=[link()], tips=[tip()])))

    def test_handle_rules_are_those_of_the_service(self):
        for bad in ["ab", "x" * 31, "-start", "has space", "end.", "a..b", "accentué", ""]:
            self.assertTrue(any("handle" in e for e in errors(dataset([creator(handle=bad)]))), bad)
        for good in ["abc", "Marie.Test_1", "@marie_test", "x" * 30]:
            self.assertFalse(any("handle" in e for e in errors(dataset([creator(handle=good)]))), good)

    def test_handles_are_unique_without_regard_to_case_and_keys_too(self):
        found = errors(dataset([creator(), creator(key="b", handle="MARIE_TEST")]))
        self.assertTrue(any("déjà utilisé" in e for e in found))
        self.assertTrue(any("clé déjà utilisée" in e for e in errors(dataset([creator(), creator(handle="autre_nom")]))))

    def test_specialties_come_from_the_taxonomy_and_are_five_at_most(self):
        self.assertTrue(any("taxonomie" in e for e in errors(dataset([creator(specialties="nature.coast|food")]))))
        six = "|".join(["nature.coast", "nature.cliffs_gorges", "nature.mountain", "nature.forest", "nature.caves", "nature.fauna"])
        self.assertTrue(any("5 spécialités" in e for e in errors(dataset([creator(specialties=six)]))))

    def test_length_limits_of_the_profile_and_the_tip(self):
        self.assertTrue(any("bio" in e for e in errors(dataset([creator(bio="x" * 301)]))))
        self.assertEqual([], [e for e in errors(dataset([creator(bio="x" * 300)])) if "bio" in e])
        self.assertTrue(any("display_name" in e for e in errors(dataset([creator(display_name="x" * 81)]))))
        self.assertTrue(any("text" in e for e in errors(dataset(tips=[tip(text="x" * 281)]))))
        self.assertEqual([], [e for e in errors(dataset(tips=[tip(text="x" * 280)])) if "text" in e])

    def test_templates_left_unfilled_and_personal_data_in_texts_are_refused(self):
        self.assertTrue(any("exemple" in e for e in errors(dataset([creator(display_name="[À COMPLÉTER nom affiché]")]))))
        self.assertTrue(any("exemple" in e for e in errors(dataset([creator(handle="exemple_createur")]))))
        self.assertTrue(any("e-mail" in e for e in errors(dataset([creator(bio="Écrivez-moi : marie@example.org")]))))
        self.assertTrue(any("e-mail" in e for e in errors(dataset(tips=[tip(text="Appelez le 06 12 34 56 78")]))))

    def test_consent_must_come_with_its_date_not_in_the_future_and_publishing_needs_it(self):
        self.assertTrue(any("futur" in e for e in errors(dataset([creator(consent_accepted_at="2026-12-01")]))))
        self.assertTrue(any("date AAAA-MM-JJ" in e for e in errors(dataset([creator(consent_accepted_at="15/10/2026")]))))
        self.assertTrue(any("sans référence" in e for e in errors(dataset([creator(consent_document_ref="")]))))
        self.assertTrue(any("sans consentement" in e for e in errors(dataset([creator(consent_document_ref="", consent_accepted_at="")]))))
        draft = creator(consent_document_ref="", consent_accepted_at="", publish="non")
        self.assertEqual([], errors(dataset([draft])))
        self.assertTrue(any("spécialité" in e for e in errors(dataset([creator(specialties="")]))))
        self.assertTrue(any("oui ou non" in e for e in errors(dataset([creator(publish="peut-être")]))))

    def test_links_languages_account_and_destinations(self):
        self.assertTrue(any("link_instagram" in e for e in errors(dataset([creator(link_instagram="http://instagram.com/x")]))))
        self.assertTrue(any("languages" in e for e in errors(dataset([creator(languages="français")]))))
        self.assertTrue(any("account_id" in e for e in errors(dataset([creator(account_id="123")]))))
        self.assertTrue(any("destination_ids" in e for e in errors(dataset([creator(destination_ids="marseille")]))))
        ok = creator(account_id="0f8fad5b-d9cb-469f-a165-70867728950e", destination_ids="0f8fad5b-d9cb-469f-a165-70867728950e", link_website="https://exemple.invalid/blog")
        self.assertEqual([], errors(dataset([ok])))

    def test_content_urls_are_the_public_ones_the_service_recognizes(self):
        for url in ["https://youtu.be/AAAAAAAAAAA", "https://youtube.com/shorts/AAAAAAAAAAA", "https://www.instagram.com/reel/Cabc123xyz/", "https://www.tiktok.com/@marie/video/7234567890123456789"]:
            self.assertIsNotNone(imp.parse_content_url(url), url)
        for url in ["http://youtu.be/AAAAAAAAAAA", "https://youtube.com/@marie", "https://www.instagram.com/marie/", "https://www.tiktok.com/@marie", "https://exemple.invalid/v", "", "https://youtu.be/short"]:
            self.assertIsNone(imp.parse_content_url(url), url)
        self.assertEqual("https://www.youtube.com/watch?v=AAAAAAAAAAA", imp.parse_content_url("https://youtu.be/AAAAAAAAAAA")[2])
        self.assertTrue(any("url" in e for e in errors(dataset(contents=[content(url="https://youtube.com/@marie")]))))

    def test_the_same_video_twice_is_refused_even_with_another_spelling(self):
        found = errors(dataset(contents=[content(), content(url="https://youtu.be/AAAAAAAAAAA")]))
        self.assertTrue(any("même contenu" in e for e in found))

    def test_chapters(self):
        self.assertEqual(([(0, "Intro"), (135, "Vallon")], None), imp.parse_chapters("2:15 Vallon|0:00 Intro"))
        self.assertEqual((3723, 1), (imp.parse_clock("1:02:03"), 1))
        self.assertIsNone(imp.parse_clock("1:75"))
        self.assertTrue(any("chapters" in e and "mm:ss" in e for e in errors(dataset(contents=[content(chapters="Intro sans heure")]))))
        self.assertTrue(any("même horodatage" in e for e in errors(dataset(contents=[content(chapters="1:00 A|1:00 B")]))))
        self.assertTrue(any("dépasse la durée" in e for e in errors(dataset(contents=[content(chapters="0:00 A|20:00 B")]))))
        self.assertTrue(any("100 caractères" in e for e in errors(dataset(contents=[content(chapters="0:00 " + "x" * 101)]))))

    def test_content_fields(self):
        self.assertTrue(any("is_commercial" in e for e in errors(dataset(contents=[content(is_commercial="parfois")]))))
        self.assertTrue(any("kind" in e for e in errors(dataset(contents=[content(kind="podcast")]))))
        self.assertTrue(any("caption_excerpt" in e for e in errors(dataset(contents=[content(caption_excerpt="x" * 501)]))))
        self.assertTrue(any("title" in e for e in errors(dataset(contents=[content(title="x" * 201)]))))
        self.assertTrue(any("published_at" in e for e in errors(dataset(contents=[content(published_at="hier")]))))

    def test_place_links_point_to_a_content_of_the_same_creator_and_a_time_within_it(self):
        self.assertTrue(any("pas dans contents.csv" in e for e in errors(dataset(contents=[], links=[link()]))))
        self.assertTrue(any("suppose un contenu" in e for e in errors(dataset(contents=[content()], links=[link(content_url="", start_time="1:00")]))))
        self.assertTrue(any("dépasse la durée" in e for e in errors(dataset(contents=[content()], links=[link(start_time="20:00")]))))
        self.assertTrue(any("status" in e for e in errors(dataset(contents=[content()], links=[link(status="published")]))))
        self.assertTrue(any("place" in e for e in errors(dataset(contents=[content()], links=[link(place="")]))))
        self.assertTrue(any("poi_id" in e for e in errors(dataset(contents=[content()], links=[link(poi_id="abc")]))))
        self.assertEqual([], errors(dataset(contents=[content()], links=[link(content_url="", start_time="", place="MuCEM")])), "a place link without content is a tip-less association")

    def test_unknown_creator_keys_and_duplicate_tips(self):
        self.assertTrue(any("creator_key" in e for e in errors(dataset(contents=[content(creator_key="zzz")]))))
        self.assertTrue(any("un seul conseil" in e for e in errors(dataset(tips=[tip(), tip(place="vallon des auffes")]))))

    def test_files_read_utf8_bom_semicolons_comments_and_report_columns(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            (folder / "creators.csv").write_bytes(
                "key;handle;display_name;mystere\n#EXEMPLE;x;y;z\na;marie_test;Marie « Test »;1\n".encode("utf-8-sig"))
            data, issues = imp.load_dataset(folder)
            self.assertEqual(1, len(data.creators))
            self.assertEqual("Marie « Test »", data.creators[0].get("display_name"))
            self.assertTrue(any(i.field == "mystere" and "inconnue" in i.message for i in issues))
            self.assertEqual(3, data.creators[0].line, "line numbers match the spreadsheet")
        with tempfile.TemporaryDirectory() as folder:
            self.assertTrue(any("obligatoire et absent" in i.message for i in imp.load_dataset(Path(folder))[1]))
            Path(folder, "creators.csv").write_text("handle\nx\n", encoding="utf-8")
            self.assertTrue(any("colonne obligatoire absente" in i.message for i in imp.load_dataset(Path(folder))[1]))

    def test_a_row_with_too_many_cells_points_at_quoting(self):
        with tempfile.TemporaryDirectory() as folder:
            Path(folder, "creators.csv").write_text("key,handle,display_name\na,marie_test,Marie, Test\n", encoding="utf-8")
            self.assertTrue(any("guillemets" in i.message for i in imp.load_dataset(Path(folder))[1]))

    def test_the_templates_are_empty_valid_files_and_the_demo_set_is_valid(self):
        data, issues = imp.load_dataset(ROOT / "docs" / "creators" / "modeles")
        self.assertEqual([], [str(i) for i in issues + imp.validate(data, TODAY)])
        self.assertEqual(0, len(data.creators))
        demo, issues = imp.load_dataset(ROOT / "docs" / "creators" / "modeles" / "demo")
        self.assertEqual([], [str(i) for i in issues + imp.validate(demo, TODAY) if i.severity == "error"])
        self.assertEqual(2, len(demo.creators))

    def test_an_uncommented_template_row_is_refused_as_a_placeholder(self):
        text = (ROOT / "docs" / "creators" / "modeles" / "creators.csv").read_text(encoding="utf-8-sig").replace("#EXEMPLE (ligne ignorée)", "x")
        with tempfile.TemporaryDirectory() as folder:
            Path(folder, "creators.csv").write_text(text, encoding="utf-8")
            data, issues = imp.load_dataset(Path(folder))
            self.assertTrue(any("exemple" in i.message or "compléter" in i.message for i in issues + imp.validate(data, TODAY)))

    def test_taxonomy_is_the_one_of_the_service(self):
        source = (ROOT / "src" / "Shared" / "OnVoyage.Taxonomy" / "Interests.cs").read_text(encoding="utf-8")
        tree = {}
        for node, children in re.findall(r'\["(\w+)"\] = \[([^\]]*)\]', source):
            tree[node] = re.findall(r'"(\w+)"', children)
        self.assertEqual(tree, imp.TAXONOMY_TREE)
        self.assertEqual(74, len(imp.TAXONOMY))


class FakeServer:
    """The admin API of Creators in memory: just enough routes and the same shapes as the DTOs."""

    def __init__(self, places=None, fail=None):
        self.creators, self.calls, self.fail = {}, [], fail or {}
        self.places = places if places is not None else [{"poiId": "poi-vallon", "name": "Vallon des Auffes"}, {"poiId": "poi-mucem", "name": "MuCEM"}]
        self.next = 0

    def new_id(self, prefix):
        self.next += 1
        return f"{prefix}-{self.next}"

    def request(self, method, path, body=None):
        self.calls.append((method, path, body))
        for pattern, status in self.fail.items():
            if re.search(pattern, f"{method} {path}"):
                return status, {"title": "refus simulé"}
        route = path.split("?")[0].replace(imp.API, "")
        if method == "GET" and route == "/places":
            query = re.search(r"query=([^&]*)", path).group(1)
            word = imp.fold(__import__("urllib.parse").parse.unquote(query))
            return 200, [p for p in self.places if word in imp.fold(p["name"])]
        if method == "GET" and route == "/creators":
            term = re.search(r"search=([^&]*)", path).group(1).lower()
            return 200, [{"id": c["id"], "handle": c["handle"]} for c in self.creators.values() if term in c["handle"].lower()]
        if method == "POST" and route == "/creators":
            cid = self.new_id("cr")
            self.creators[cid] = {"id": cid, "handle": body["handle"], "status": "draft", "termsDocumentRef": None, "accountId": None, "contents": [], "placeLinks": [], "tips": []}
            return 201, {"id": cid}
        match = re.fullmatch(r"/creators/([\w-]+)(/[\w-]+(?:/[\w-]+)?)?", route)
        if not match or match.group(1) not in self.creators:
            return 404, {"title": "introuvable"}
        c, sub = self.creators[match.group(1)], match.group(2)
        if sub is None and method == "GET":
            return 200, c
        if sub is None and method == "PUT":
            return 200, c
        if sub == "/consent":
            c["termsDocumentRef"] = body["documentRef"]
            return 200, c
        if sub == "/account":
            c["accountId"] = body["accountId"]
            return 200, c
        if sub == "/contents":
            parsed = imp.parse_content_url(body["url"])
            cid = self.new_id("ct")
            c["contents"].append({"id": cid, "permalink": parsed[2]})
            return 201, {"id": cid}
        if sub == "/place-links":
            c["placeLinks"].append({"poiId": body["poiId"], "contentId": body["contentId"], "startSeconds": body["startSeconds"]})
            return 200, {"id": self.new_id("pl")}
        if sub and sub.startswith("/tips/"):
            c["tips"] = [t for t in c["tips"] if t["poiId"] != sub[6:]] + [{"poiId": sub[6:], "text": body["text"]}]
            return 200, {"id": self.new_id("tp")}
        if sub == "/publish":
            c["status"] = "published"
            return 200, c
        return 404, {"title": "route inconnue"}


def posts(server):
    return [(m, p) for m, p, _ in server.calls if m != "GET"]


class Import(unittest.TestCase):
    def full(self):
        return dataset(contents=[content()], links=[link(), link(place="MuCEM", content_url="", start_time="")], tips=[tip()])

    def run_import(self, server, data=None, **kw):
        return imp.import_dataset(data or self.full(), server, out=lambda _: None, **kw)

    def test_a_first_import_creates_everything_in_order_and_publishes(self):
        server = FakeServer()
        summary = self.run_import(server)
        self.assertEqual([], summary.problems)
        kinds = [(m, re.sub(r"/(cr|ct|poi)-[\w-]+", "/ID", p.replace(imp.API, ""))) for m, p in posts(server)]
        self.assertEqual([("POST", "/creators"), ("PUT", "/creators/ID/consent"), ("POST", "/creators/ID/contents"), ("POST", "/creators/ID/place-links"),
                          ("POST", "/creators/ID/place-links"), ("PUT", "/creators/ID/tips/ID"), ("POST", "/creators/ID/publish")], kinds)
        stored = next(iter(server.creators.values()))
        self.assertEqual("published", stored["status"])
        self.assertEqual("FONDATEUR-TEST-001", stored["termsDocumentRef"])
        self.assertEqual(135, stored["placeLinks"][0]["startSeconds"])
        self.assertEqual(stored["contents"][0]["id"], stored["placeLinks"][0]["contentId"], "the link points to the content created in the same run")

    def test_the_request_bodies_follow_the_service_contracts(self):
        server = FakeServer()
        self.run_import(server)
        profile = next(b for m, p, b in server.calls if m == "POST" and p.endswith("/creators"))
        self.assertEqual({"handle", "displayName", "bio", "avatarPath", "languages", "specialties", "destinationIds", "links"}, set(profile))
        consent = next(b for m, p, b in server.calls if p.endswith("/consent"))
        self.assertEqual({"documentRef": "FONDATEUR-TEST-001", "acceptedAt": "2026-09-15T00:00:00Z"}, consent)
        added = next(b for m, p, b in server.calls if p.endswith("/contents"))
        self.assertEqual({"url", "title", "captionExcerpt", "coverPath", "publishedAt", "durationSeconds", "kind", "isCommercial", "chapters"}, set(added))
        self.assertEqual([{"startSeconds": 0, "title": "Intro"}, {"startSeconds": 135, "title": "Vallon des Auffes"}], added["chapters"])
        link_body = next(b for m, p, b in server.calls if p.endswith("/place-links"))
        self.assertEqual({"poiId", "contentId", "startSeconds", "status"}, set(link_body))
        self.assertEqual({"text": "Venir en fin de journée."}, next(b for m, p, b in server.calls if "/tips/" in p))

    def test_running_it_twice_changes_nothing_the_second_time(self):
        server = FakeServer()
        self.run_import(server)
        before = len(posts(server))
        second = self.run_import(server)
        self.assertEqual([], second.problems)
        self.assertEqual([], [c for c in posts(server)[before:]], "no write on the second run")
        self.assertTrue(any("déjà" in line for line in second.skipped))

    def test_an_existing_profile_is_not_overwritten_unless_asked(self):
        server = FakeServer()
        self.run_import(server)
        n = len(posts(server))
        self.run_import(server, update_profiles=True)
        self.assertEqual([("PUT", f"{imp.API}/creators/cr-1")], posts(server)[n:])

    def test_a_draft_stays_a_draft(self):
        server = FakeServer()
        self.run_import(server, dataset([creator(publish="non")]))
        self.assertEqual("draft", next(iter(server.creators.values()))["status"])
        self.assertFalse(any(p.endswith("/publish") for _, p in posts(server)))

    def test_an_unknown_or_ambiguous_place_is_reported_never_guessed(self):
        server = FakeServer(places=[{"poiId": "p1", "name": "Calanque de Sormiou"}, {"poiId": "p2", "name": "Calanque de Sugiton"}])
        summary = self.run_import(server, dataset(tips=[tip(place="Calanque")]))
        self.assertTrue(any("sans correspondance exacte" in p and "Sormiou" in p for p in summary.problems))
        summary = self.run_import(FakeServer(places=[]), dataset(tips=[tip(place="Lieu inconnu")]))
        self.assertTrue(any("introuvable" in p for p in summary.problems))
        twins = FakeServer(places=[{"poiId": "p1", "name": "Mucem"}, {"poiId": "p2", "name": "MUCEM"}])
        self.assertTrue(any("ambigu" in p for p in self.run_import(twins, dataset(tips=[tip(place="mucem")])).problems))

    def test_accents_and_case_do_not_matter_when_matching_a_place(self):
        server = FakeServer(places=[{"poiId": "poi-garde", "name": "Notre-Dame de la Garde"}])
        self.assertEqual([], self.run_import(server, dataset(tips=[tip(place="notre dame de la garde")])).problems)
        server = FakeServer(places=[{"poiId": "poi-cite", "name": "Cité radieuse"}])
        self.assertEqual([], self.run_import(server, dataset(tips=[tip(place="Cite Radieuse")])).problems)

    def test_poi_id_skips_the_search(self):
        server = FakeServer(places=[])
        summary = self.run_import(server, dataset(tips=[tip(place="", poi_id="0f8fad5b-d9cb-469f-a165-70867728950e")]))
        self.assertEqual([], summary.problems)
        self.assertFalse(any("/places" in p for _, p, _ in server.calls))

    def test_a_server_refusal_is_reported_and_the_run_goes_on(self):
        server = FakeServer(fail={r"POST .*/contents": 422})
        summary = self.run_import(server)
        self.assertTrue(any("contenu" in p and "422" in p for p in summary.problems))
        self.assertTrue(any("ignorée, contenu absent" in p for p in summary.problems))
        self.assertTrue(any(c.endswith("/publish") for _, c in posts(server)))

    def test_an_expired_token_stops_everything_with_advice(self):
        server = FakeServer(fail={r"GET .*/creators\?": 401})
        summary = self.run_import(server, dataset([creator(), creator(key="b", handle="autre_nom")]))
        self.assertTrue(any("login" in p for p in summary.problems))
        self.assertEqual([], posts(server))
        self.assertEqual(1, len([1 for m, p, _ in server.calls if m == "GET"]), "stops at the first 401")

    def test_publication_refused_by_the_service_is_a_problem(self):
        server = FakeServer(fail={r"POST .*/publish": 409})
        self.assertTrue(any("publication refusée" in p for p in self.run_import(server).problems))


class Cli(unittest.TestCase):
    def folder(self, creators, extra=None):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        for name, rows in {"creators.csv": creators, **(extra or {})}.items():
            with open(Path(tmp.name, name), "w", encoding="utf-8", newline="") as handle:
                writer = csv.DictWriter(handle, fieldnames=imp.COLUMNS[name], delimiter=";")
                writer.writeheader()
                writer.writerows(rows)
        return tmp.name

    def run_cli(self, *args, env=None):
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.dict(os.environ, env or {}, clear=False), redirect_stdout(out), redirect_stderr(err):
            code = imp.main(list(args))
        return code, out.getvalue(), err.getvalue()

    def test_validate_exit_codes(self):
        good = self.folder([creator()])
        self.assertEqual(0, self.run_cli("validate", good)[0])
        bad = self.folder([creator(handle="x")])
        code, out, err = self.run_cli("validate", bad)
        self.assertEqual(1, code)
        self.assertIn("creators.csv:2 handle", err)

    def test_dry_run_prints_the_calls_writes_nothing_and_needs_no_token(self):
        folder = self.folder([creator()], {"contents.csv": [content()], "tips.csv": [tip()]})
        with mock.patch.object(imp.HttpTransport, "request", side_effect=AssertionError("no network in dry-run")):
            code, out, _ = self.run_cli("dry-run", folder, env={"ONVOYAGE_ADMIN_TOKEN": "SECRETTOKEN"})
        self.assertEqual(0, code)
        self.assertIn("POST /api/creators/v1/admin/creators", out)
        self.assertIn("PUT /api/creators/v1/admin/creators/<creator:marie_test>/consent", out)
        self.assertNotIn("SECRETTOKEN", out)
        self.assertIn("Rien n'a été écrit", out)

    def test_dry_run_in_curl_format_never_contains_the_token_only_the_variable(self):
        folder = self.folder([creator()])
        code, out, _ = self.run_cli("dry-run", folder, "--format", "curl", "--base-url", "https://staging.example.org", env={"ONVOYAGE_ADMIN_TOKEN": "SECRETTOKEN"})
        self.assertEqual(0, code)
        self.assertIn("curl -sS -X POST https://staging.example.org/api/creators/v1/admin/creators", out)
        self.assertIn("$ONVOYAGE_ADMIN_TOKEN", out)
        self.assertNotIn("SECRETTOKEN", out)

    def test_apply_needs_an_https_gateway_and_a_token_from_the_environment(self):
        folder = self.folder([creator()])
        for args, env in [(("apply", folder), {}), (("apply", folder, "--base-url", "http://staging.example.org"), {"ONVOYAGE_ADMIN_TOKEN": "t"}),
                          (("apply", folder, "--base-url", "https://staging.example.org"), {"ONVOYAGE_ADMIN_TOKEN": ""})]:
            with mock.patch.dict(os.environ, {"ONVOYAGE_ADMIN_TOKEN": ""}, clear=False), self.assertRaises(SystemExit):
                with mock.patch.dict(os.environ, env), redirect_stdout(io.StringIO()):
                    imp.main(list(args))

    def test_apply_with_a_valid_dataset_calls_the_transport_and_never_prints_the_token(self):
        folder = self.folder([creator(publish="non")])
        server = FakeServer()
        with mock.patch.object(imp, "HttpTransport", return_value=server) as transport:
            code, out, err = self.run_cli("apply", folder, "--base-url", "https://staging.example.org", env={"ONVOYAGE_ADMIN_TOKEN": "SECRETTOKEN"})
        self.assertEqual(0, code)
        transport.assert_called_once_with("https://staging.example.org", "SECRETTOKEN")
        self.assertNotIn("SECRETTOKEN", out + err)
        self.assertEqual(1, len(server.creators))

    def test_apply_refuses_the_demonstration_set(self):
        code, _, err = self.run_cli("apply", str(ROOT / "docs" / "creators" / "modeles" / "demo"), "--base-url", "https://staging.example.org", env={"ONVOYAGE_ADMIN_TOKEN": "t"})
        self.assertEqual(1, code)
        self.assertIn("démonstration", err)

    def test_nothing_is_sent_when_the_files_are_invalid(self):
        folder = self.folder([creator(handle="x")])
        with mock.patch.object(imp, "HttpTransport") as transport:
            code, _, _ = self.run_cli("apply", folder, "--base-url", "https://staging.example.org", env={"ONVOYAGE_ADMIN_TOKEN": "t"})
        self.assertEqual(1, code)
        transport.assert_not_called()

    def test_the_request_json_is_serializable(self):
        row = imp.Row(2, content())
        self.assertEqual(754, json.loads(json.dumps(imp.content_body(row)))["durationSeconds"])


if __name__ == "__main__":
    unittest.main()
