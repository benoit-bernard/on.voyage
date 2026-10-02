#!/usr/bin/env python3
"""
import-founder-creators : valide les CSV des créateurs fondateurs (H-009) puis les importe par l'API d'administration du service Creators.

    python3 tools/import-founder-creators/import_founder_creators.py validate  DOSSIER
    python3 tools/import-founder-creators/import_founder_creators.py dry-run   DOSSIER [--format http|curl] [--read-online --base-url URL]
    python3 tools/import-founder-creators/import_founder_creators.py apply     DOSSIER --base-url https://staging.on.voyage        (jeton : ONVOYAGE_ADMIN_TOKEN)
    python3 tools/import-founder-creators/import_founder_creators.py login     --base-url https://staging.on.voyage --email admin@...

DOSSIER contient `creators.csv` (obligatoire), `contents.csv`, `place_links.csv`, `tips.csv` (modèles : docs/creators/modeles/). Les routes appelées sont
celles de `src/Services/Creators/OnVoyage.Creators.Api/Endpoints/CreatorsEndpoints.cs`, par le Gateway (`/api/creators/v1/admin/**`, politique `admin`).
L'import est rejouable : un créateur, un contenu, une association ou un conseil déjà présent n'est pas dupliqué. Rien n'est écrit sans `apply`.
Bibliothèque standard Python 3.9+ seulement. Le jeton n'est jamais affiché ni accepté sur la ligne de commande.
"""
from __future__ import annotations

import argparse
import csv
import getpass
import json
import os
import re
import shlex
import sys
import unicodedata
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Any, Callable, Iterable, Protocol
from urllib.parse import quote, urlparse, parse_qs

API = "/api/creators/v1/admin"

# ---- Rules copied from src/Services/Creators (Domain): Handles, CreatorProfileRules, ContentItem, ContentRules, CreatorTip. The tests compare the taxonomy with Interests.cs.
HANDLE_MIN, HANDLE_MAX = 3, 30
MAX_DISPLAY_NAME, MAX_BIO, MAX_SPECIALTIES, MAX_LANGUAGES, MAX_LINKS, MAX_PATH = 80, 300, 5, 6, 10, 300
MAX_TITLE, MAX_EXCERPT, MAX_CHAPTERS, MAX_CHAPTER_TITLE, MAX_TIP, MAX_DOCUMENT_REF = 200, 500, 100, 100, 280, 200
LINK_KINDS = ("instagram", "youtube", "tiktok", "website")
CONTENT_KINDS = ("video", "photo", "carousel", "article")
PLACE_LINK_STATUSES = ("validated", "proposed", "rejected")

TAXONOMY_TREE = {
    "history": ["antiquity", "middle_ages", "renaissance", "early_modern", "revolution_empire", "19th_century", "world_wars", "military", "maritime", "industrial", "local"],
    "architecture": ["romanesque", "gothic", "classical", "baroque", "19th_century", "modern", "defensive", "religious", "vernacular", "industrial"],
    "nature": ["coast", "cliffs_gorges", "mountain", "forest", "wetlands", "geology", "caves", "rivers_waterfalls", "flora", "fauna", "viewpoints"],
    "culture": ["museums", "painting", "contemporary_art", "literature", "cinema", "music", "crafts", "traditions", "street_art"],
    "religion": ["churches", "abbeys", "pilgrimage"],
    "villages": ["perched", "fishing", "remarkable"],
    "gastronomy": ["local_cuisine", "wine", "markets", "producers", "olive_oil", "cheese"],
    "curiosities": ["science", "astronomy", "legends", "engineering"],
    "outdoors": ["hiking", "water_sports", "cycling", "bivouac"],
    "leisure": ["beaches", "parks_gardens", "family"],
}
TAXONOMY = frozenset(code for node, children in TAXONOMY_TREE.items() for code in [node, *[f"{node}.{child}" for child in children]])

HANDLE_CHARS = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._]*$")
GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
PHONE = re.compile(r"(?<!\d)(?:\+?\d[\d .-]{8,}\d)(?!\d)")
PLACEHOLDER = re.compile(r"\[\s*(?:À|A)\s*COMPL[ÉE]TER[^\]]*\]|^EXEMPLE\b|<[^>]+>", re.IGNORECASE)
YOUTUBE_ID = re.compile(r"^[A-Za-z0-9_-]{11}$")
INSTAGRAM_CODE = re.compile(r"^[A-Za-z0-9_-]{5,30}$")
TIKTOK_ID = re.compile(r"^[0-9]{6,25}$")
TIKTOK_USER = re.compile(r"^@[A-Za-z0-9._]{1,40}$")


# ---------------------------------------------------------------------------------------------------------------------- model
@dataclass
class Issue:
    file: str
    line: int
    field: str
    message: str
    severity: str = "error"

    def __str__(self) -> str:
        where = f"{self.file}:{self.line}" if self.line else self.file
        return f"[{'ERREUR' if self.severity == 'error' else 'avertissement'}] {where} {self.field}: {self.message}"


@dataclass
class Row:
    line: int
    values: dict[str, str]

    def get(self, name: str) -> str:
        return (self.values.get(name) or "").strip()


@dataclass
class Dataset:
    creators: list[Row] = field(default_factory=list)
    contents: list[Row] = field(default_factory=list)
    place_links: list[Row] = field(default_factory=list)
    tips: list[Row] = field(default_factory=list)


COLUMNS = {
    "creators.csv": ["key", "handle", "display_name", "bio", "avatar_path", "languages", "specialties", "destination_ids", "link_instagram", "link_youtube",
                     "link_tiktok", "link_website", "consent_document_ref", "consent_accepted_at", "account_id", "publish"],
    "contents.csv": ["creator_key", "url", "title", "caption_excerpt", "cover_path", "published_at", "duration_seconds", "kind", "is_commercial", "chapters"],
    "place_links.csv": ["creator_key", "place", "poi_id", "content_url", "start_time", "status"],
    "tips.csv": ["creator_key", "place", "poi_id", "text"],
}
REQUIRED_COLUMNS = {
    "creators.csv": ["key", "handle", "display_name"],
    "contents.csv": ["creator_key", "url", "title"],
    "place_links.csv": ["creator_key"],
    "tips.csv": ["creator_key", "text"],
}


# ---------------------------------------------------------------------------------------------------------------------- reading
def fold(text: str) -> str:
    """Lower case without accents or punctuation, like PoiEntry.Fold on the server (used to compare place names)."""
    decomposed = unicodedata.normalize("NFD", text)
    plain = "".join(c for c in decomposed if unicodedata.category(c) != "Mn")
    return re.sub(r"[^a-z0-9]+", " ", plain.lower()).strip()


def read_csv(path: Path, issues: list[Issue]) -> list[Row]:
    name = path.name
    try:
        text = path.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError) as error:
        issues.append(Issue(name, 0, "fichier", f"illisible ({error}); enregistrez en UTF-8"))
        return []
    sample = text[:4096]
    delimiter = ";" if sample.count(";") > sample.count(",") else ","  # Excel in French writes ';'
    reader = csv.reader(text.splitlines(), delimiter=delimiter)
    try:
        header = [column.strip() for column in next(reader)]
    except StopIteration:
        issues.append(Issue(name, 0, "en-têtes", "fichier vide"))
        return []
    expected = COLUMNS[name]
    for column in header:
        if column and column not in expected:
            issues.append(Issue(name, 1, column, f"colonne inconnue (attendues : {', '.join(expected)})"))
    for column in REQUIRED_COLUMNS[name]:
        if column not in header:
            issues.append(Issue(name, 1, column, "colonne obligatoire absente"))
    rows: list[Row] = []
    for number, cells in enumerate(reader, start=2):
        if not any(cell.strip() for cell in cells) or cells[0].lstrip().startswith("#"):
            continue  # blank line or comment row (the templates carry their example as a comment)
        if len(cells) > len(header):
            issues.append(Issue(name, number, "ligne", f"{len(cells)} cellules pour {len(header)} colonnes (un ';' ou une virgule dans un texte ? mettez-le entre guillemets)"))
            cells = cells[:len(header)]
        rows.append(Row(number, dict(zip(header, cells))))
    return rows


def load_dataset(folder: Path) -> tuple[Dataset, list[Issue]]:
    issues: list[Issue] = []
    data = Dataset()
    if not folder.is_dir():
        return data, [Issue(str(folder), 0, "dossier", "introuvable")]
    for name, target in (("creators.csv", "creators"), ("contents.csv", "contents"), ("place_links.csv", "place_links"), ("tips.csv", "tips")):
        path = folder / name
        if path.exists():
            setattr(data, target, read_csv(path, issues))
        elif name == "creators.csv":
            issues.append(Issue(name, 0, "fichier", "obligatoire et absent"))
    return data, issues


# ---------------------------------------------------------------------------------------------------------------------- parsing helpers
def parse_clock(text: str) -> int | None:
    """`2:15` -> 135, `1:02:03` -> 3723, `95` -> 95 (seconds). None when it is not a time."""
    text = text.strip()
    if re.fullmatch(r"\d{1,6}", text):
        return int(text)
    match = re.fullmatch(r"(?:(\d{1,2}):)?(\d{1,2}):(\d{2})", text)
    if not match:
        return None
    hours, minutes, seconds = int(match.group(1) or 0), int(match.group(2)), int(match.group(3))
    return None if seconds > 59 or (match.group(1) and minutes > 59) else hours * 3600 + minutes * 60 + seconds


def parse_bool(text: str) -> bool | None:
    value = text.strip().lower()
    if value in ("", "non", "no", "false", "0", "n"):
        return False
    if value in ("oui", "yes", "true", "1", "o", "y", "x"):
        return True
    return None


def parse_date(text: str) -> datetime | None:
    try:
        parsed = datetime.fromisoformat(text.strip().replace("Z", "+00:00"))
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def parse_content_url(url: str) -> tuple[str, str, str, str] | None:
    """(platform, external id, canonical permalink, default kind), like ContentUrls.Parse; None when the URL is not a public video or post."""
    if not url or len(url) > 500:
        return None
    parsed = urlparse(url.strip())
    if parsed.scheme != "https" or not parsed.hostname:
        return None
    host = parsed.hostname.lower()
    for prefix in ("www.", "m."):
        if host.startswith(prefix):
            host = host[len(prefix):]
    segments = [s for s in parsed.path.split("/") if s]
    if host == "youtube.com":
        video = parse_qs(parsed.query).get("v", [None])[0] if segments == ["watch"] else (segments[1] if len(segments) == 2 and segments[0] in ("shorts", "embed", "live") else None)
        if video and YOUTUBE_ID.match(video):
            return "youtube", video, f"https://www.youtube.com/watch?v={video}", "video"
    elif host == "youtu.be" and len(segments) == 1 and YOUTUBE_ID.match(segments[0]):
        return "youtube", segments[0], f"https://www.youtube.com/watch?v={segments[0]}", "video"
    elif host == "instagram.com" and len(segments) >= 2 and segments[0] in ("p", "reel", "reels", "tv") and INSTAGRAM_CODE.match(segments[1]):
        kind = "reel" if segments[0] == "reels" else segments[0]
        return "instagram", segments[1], f"https://www.instagram.com/{kind}/{segments[1]}/", "photo" if segments[0] == "p" else "video"
    elif host == "tiktok.com" and len(segments) == 3 and TIKTOK_USER.match(segments[0]) and segments[1] == "video" and TIKTOK_ID.match(segments[2]):
        return "tiktok", segments[2], f"https://www.tiktok.com/{segments[0]}/video/{segments[2]}", "video"
    return None


def parse_chapters(text: str) -> tuple[list[tuple[int, str]], str | None]:
    """`0:00 Introduction|2:15 Vieux-Port` -> [(0, 'Introduction'), (135, 'Vieux-Port')]. Second item: error message."""
    chapters: list[tuple[int, str]] = []
    for part in [p.strip() for p in text.split("|") if p.strip()]:
        pieces = part.split(None, 1)
        start = parse_clock(pieces[0]) if pieces else None
        if start is None or len(pieces) < 2 or not pieces[1].strip():
            return [], f"chapitre « {part} » : attendu « mm:ss Titre »"
        chapters.append((start, pieces[1].strip()))
    return sorted(chapters), None


# ---------------------------------------------------------------------------------------------------------------------- validation
def check_text(issues: list[Issue], file: str, row: Row, column: str, limit: int | None = None, required: bool = False) -> str:
    value = row.get(column)
    if required and not value:
        issues.append(Issue(file, row.line, column, "obligatoire"))
    if limit is not None and len(value) > limit:
        issues.append(Issue(file, row.line, column, f"{len(value)} caractères, {limit} au plus"))
    if PLACEHOLDER.search(value):
        issues.append(Issue(file, row.line, column, "valeur d'exemple ou à compléter : remplacez-la"))
    if EMAIL.search(value) or PHONE.search(value):
        issues.append(Issue(file, row.line, column, "adresse e-mail ou numéro de téléphone dans un texte public : retirez-le (minimisation des données)"))
    return value


def split_list(value: str) -> list[str]:
    return [item.strip() for item in re.split(r"[|]", value) if item.strip()]


def validate(data: Dataset, today: date | None = None) -> list[Issue]:
    issues: list[Issue] = []
    today = today or datetime.now(timezone.utc).date()
    keys: dict[str, Row] = {}
    handles: dict[str, Row] = {}

    for row in data.creators:
        f = "creators.csv"
        key = check_text(issues, f, row, "key", required=True)
        if key and not re.fullmatch(r"[a-z0-9_-]{1,40}", key):
            issues.append(Issue(f, row.line, "key", "clé locale : minuscules, chiffres, - et _ (40 au plus)"))
        if key in keys:
            issues.append(Issue(f, row.line, "key", f"clé déjà utilisée ligne {keys[key].line}"))
        keys.setdefault(key, row)

        handle = row.get("handle").lstrip("@")
        if not (HANDLE_MIN <= len(handle) <= HANDLE_MAX and HANDLE_CHARS.match(handle) and not handle.endswith(".") and ".." not in handle):
            issues.append(Issue(f, row.line, "handle", f"{HANDLE_MIN} à {HANDLE_MAX} caractères : lettres, chiffres, point et tiret bas, sans point final ni deux points de suite"))
        elif PLACEHOLDER.search(handle) or handle.lower().startswith("exemple"):
            issues.append(Issue(f, row.line, "handle", "pseudonyme d'exemple : remplacez-le"))
        if handle.lower() in handles:
            issues.append(Issue(f, row.line, "handle", f"déjà utilisé ligne {handles[handle.lower()].line} (l'unicité ignore la casse)"))
        handles.setdefault(handle.lower(), row)

        check_text(issues, f, row, "display_name", MAX_DISPLAY_NAME, required=True)
        check_text(issues, f, row, "bio", MAX_BIO)
        avatar = check_text(issues, f, row, "avatar_path", MAX_PATH)
        if any(c.isspace() for c in avatar):
            issues.append(Issue(f, row.line, "avatar_path", "ne doit pas contenir d'espace"))

        languages = [lang.lower() for lang in split_list(row.get("languages"))]
        if len(set(languages)) > MAX_LANGUAGES or any(not re.fullmatch(r"[a-z]{2,3}", lang) for lang in languages):
            issues.append(Issue(f, row.line, "languages", f"codes de 2 ou 3 lettres séparés par | ({MAX_LANGUAGES} au plus), ex. fr|en"))

        specialties = split_list(row.get("specialties"))
        if len(set(specialties)) > MAX_SPECIALTIES:
            issues.append(Issue(f, row.line, "specialties", f"{MAX_SPECIALTIES} spécialités au plus"))
        for code in specialties:
            if code not in TAXONOMY:
                issues.append(Issue(f, row.line, "specialties", f"« {code} » n'est pas dans la taxonomie v1 (annexe D), ex. nature.coast|history.local"))

        for guid in split_list(row.get("destination_ids")):
            if not GUID.match(guid):
                issues.append(Issue(f, row.line, "destination_ids", f"« {guid} » n'est pas un identifiant (GUID) de destination"))

        links = 0
        for kind in LINK_KINDS:
            link = row.get(f"link_{kind}")
            if not link:
                continue
            links += 1
            parsed = urlparse(link)
            if parsed.scheme != "https" or not parsed.hostname or len(link) > MAX_PATH or PLACEHOLDER.search(link):
                issues.append(Issue(f, row.line, f"link_{kind}", "adresse https:// attendue (300 caractères au plus)"))
        if links > MAX_LINKS:
            issues.append(Issue(f, row.line, "links", f"{MAX_LINKS} liens au plus"))

        ref = check_text(issues, f, row, "consent_document_ref", MAX_DOCUMENT_REF)
        accepted = row.get("consent_accepted_at")
        if ref and not accepted:
            issues.append(Issue(f, row.line, "consent_accepted_at", "date de signature du consentement obligatoire avec sa référence", "warning"))
        if accepted:
            when = parse_date(accepted)
            if when is None:
                issues.append(Issue(f, row.line, "consent_accepted_at", "date AAAA-MM-JJ attendue"))
            elif when.date() > today:
                issues.append(Issue(f, row.line, "consent_accepted_at", "la date du consentement ne peut pas être dans le futur"))
            if not ref:
                issues.append(Issue(f, row.line, "consent_document_ref", "date sans référence du document signé"))
        if row.get("account_id") and not GUID.match(row.get("account_id")):
            issues.append(Issue(f, row.line, "account_id", "identifiant de compte (GUID) attendu ; laissez vide tant que le créateur ne s'est pas connecté"))

        publish = parse_bool(row.get("publish"))
        if publish is None:
            issues.append(Issue(f, row.line, "publish", "oui ou non"))
        elif publish:
            if not ref or not accepted:
                issues.append(Issue(f, row.line, "publish", "publication impossible sans consentement fondateur (référence du document et date) - F-26"))
            if not specialties:
                issues.append(Issue(f, row.line, "publish", "publication impossible sans au moins une spécialité"))

    contents_by_creator: dict[str, dict[str, dict[str, Any]]] = {}
    seen_external: dict[tuple[str, str], Row] = {}
    for row in data.contents:
        f = "contents.csv"
        owner = row.get("creator_key")
        if owner not in keys:
            issues.append(Issue(f, row.line, "creator_key", f"« {owner} » n'existe pas dans creators.csv"))
        check_text(issues, f, row, "title", MAX_TITLE, required=True)
        check_text(issues, f, row, "caption_excerpt", MAX_EXCERPT)
        cover = check_text(issues, f, row, "cover_path", MAX_PATH)
        if any(c.isspace() for c in cover):
            issues.append(Issue(f, row.line, "cover_path", "ne doit pas contenir d'espace"))
        parsed = parse_content_url(row.get("url"))
        if parsed is None:
            issues.append(Issue(f, row.line, "url", "adresse https d'une vidéo ou publication publique YouTube (watch, youtu.be, shorts), Instagram (p, reel, tv) ou TikTok (@compte/video/n°)"))
        else:
            platform, external, permalink, _ = parsed
            if (platform, external) in seen_external:
                issues.append(Issue(f, row.line, "url", f"même contenu que la ligne {seen_external[(platform, external)].line}"))
            seen_external.setdefault((platform, external), row)
        duration = None
        if row.get("duration_seconds"):
            duration = parse_clock(row.get("duration_seconds"))
            if duration is None:
                issues.append(Issue(f, row.line, "duration_seconds", "durée en secondes ou mm:ss"))
        if row.get("published_at") and parse_date(row.get("published_at")) is None:
            issues.append(Issue(f, row.line, "published_at", "date AAAA-MM-JJ attendue"))
        if row.get("kind") and row.get("kind").lower() not in CONTENT_KINDS:
            issues.append(Issue(f, row.line, "kind", f"parmi {', '.join(CONTENT_KINDS)} (ou vide)"))
        if parse_bool(row.get("is_commercial")) is None:
            issues.append(Issue(f, row.line, "is_commercial", "oui ou non (oui = mention « Publicité »)"))
        chapters, error = parse_chapters(row.get("chapters"))
        if error:
            issues.append(Issue(f, row.line, "chapters", error))
        else:
            if len(chapters) > MAX_CHAPTERS:
                issues.append(Issue(f, row.line, "chapters", f"{MAX_CHAPTERS} chapitres au plus"))
            if len({start for start, _ in chapters}) != len(chapters):
                issues.append(Issue(f, row.line, "chapters", "deux chapitres au même horodatage"))
            for start, title in chapters:
                if len(title) > MAX_CHAPTER_TITLE:
                    issues.append(Issue(f, row.line, "chapters", f"titre de chapitre de plus de {MAX_CHAPTER_TITLE} caractères"))
                if duration is not None and start > duration:
                    issues.append(Issue(f, row.line, "chapters", f"le chapitre à {start} s dépasse la durée ({duration} s)"))
                if PLACEHOLDER.search(title) or EMAIL.search(title):
                    issues.append(Issue(f, row.line, "chapters", "titre d'exemple ou contenant une adresse e-mail"))
        if parsed is not None:
            contents_by_creator.setdefault(owner, {})[parsed[2]] = {"duration": duration, "row": row}

    place_seen: set[tuple[str, str, str, int | None]] = set()
    for row in data.place_links:
        f = "place_links.csv"
        owner = row.get("creator_key")
        if owner not in keys:
            issues.append(Issue(f, row.line, "creator_key", f"« {owner} » n'existe pas dans creators.csv"))
        place, poi = check_text(issues, f, row, "place", 200), row.get("poi_id")
        if not place and not poi:
            issues.append(Issue(f, row.line, "place", "nom du lieu (ou poi_id) obligatoire"))
        if poi and not GUID.match(poi):
            issues.append(Issue(f, row.line, "poi_id", "identifiant de lieu (GUID) attendu"))
        status = (row.get("status") or "validated").lower()
        if status not in PLACE_LINK_STATUSES:
            issues.append(Issue(f, row.line, "status", f"parmi {', '.join(PLACE_LINK_STATUSES)} (vide = validated)"))
        content_url = row.get("content_url")
        parsed = parse_content_url(content_url) if content_url else None
        start = parse_clock(row.get("start_time")) if row.get("start_time") else None
        if content_url and parsed is None:
            issues.append(Issue(f, row.line, "content_url", "adresse de contenu non reconnue"))
        elif parsed is not None and parsed[2] not in contents_by_creator.get(owner, {}):
            issues.append(Issue(f, row.line, "content_url", "ce contenu n'est pas dans contents.csv pour ce créateur"))
        if row.get("start_time"):
            if start is None:
                issues.append(Issue(f, row.line, "start_time", "horodatage mm:ss ou secondes"))
            elif not content_url:
                issues.append(Issue(f, row.line, "start_time", "un horodatage suppose un contenu (content_url)"))
            elif parsed is not None:
                duration = contents_by_creator.get(owner, {}).get(parsed[2], {}).get("duration")
                if duration is not None and start > duration:
                    issues.append(Issue(f, row.line, "start_time", f"dépasse la durée du contenu ({duration} s)"))
        identity = (owner, fold(place) or poi.lower(), parsed[2] if parsed else "", start)
        if identity in place_seen:
            issues.append(Issue(f, row.line, "place", "association en double", "warning"))
        place_seen.add(identity)

    tip_seen: set[tuple[str, str]] = set()
    for row in data.tips:
        f = "tips.csv"
        owner = row.get("creator_key")
        if owner not in keys:
            issues.append(Issue(f, row.line, "creator_key", f"« {owner} » n'existe pas dans creators.csv"))
        place, poi = check_text(issues, f, row, "place", 200), row.get("poi_id")
        if not place and not poi:
            issues.append(Issue(f, row.line, "place", "nom du lieu (ou poi_id) obligatoire"))
        if poi and not GUID.match(poi):
            issues.append(Issue(f, row.line, "poi_id", "identifiant de lieu (GUID) attendu"))
        check_text(issues, f, row, "text", MAX_TIP, required=True)
        identity = (owner, fold(place) or poi.lower())
        if identity in tip_seen:
            issues.append(Issue(f, row.line, "place", "un seul conseil par lieu et par créateur"))
        tip_seen.add(identity)
    return issues


def has_errors(issues: Iterable[Issue]) -> bool:
    return any(issue.severity == "error" for issue in issues)


# ---------------------------------------------------------------------------------------------------------------------- transport
class Transport(Protocol):
    def request(self, method: str, path: str, body: Any = None) -> tuple[int, Any]: ...


class HttpTransport:
    def __init__(self, base_url: str, token: str, timeout: float = 30.0) -> None:
        self.base = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout

    def request(self, method: str, path: str, body: Any = None) -> tuple[int, Any]:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("Authorization", f"Bearer {self.token}")
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:  # noqa: S310 - https URL checked by the caller
                return response.status, _json(response.read())
        except urllib.error.HTTPError as error:
            return error.code, _json(error.read())
        except urllib.error.URLError as error:
            return 0, {"title": f"connexion impossible : {error.reason}"}


def _json(raw: bytes) -> Any:
    if not raw:
        return None
    try:
        return json.loads(raw)
    except ValueError:
        return {"title": raw[:200].decode("utf-8", "replace")}


class DryRunTransport:
    """Prints the calls instead of making them. Reads either answer 'nothing there yet' or, with `reader`, go to the real server (read-only)."""

    def __init__(self, out: Callable[[str], None], fmt: str = "http", reader: Transport | None = None, base_url: str = "$BASE_URL") -> None:
        self.out, self.fmt, self.reader, self.base = out, fmt, reader, base_url.rstrip("/")
        self.offline = reader is None  # place names cannot be resolved without the server: shown as <poiId:name>
        self.counter = 0

    def request(self, method: str, path: str, body: Any = None) -> tuple[int, Any]:
        if method == "GET" and self.reader is not None:
            return self.reader.request(method, path, body)
        if self.fmt == "curl":
            parts = ["curl", "-sS", "-X", method, shlex.quote(self.base + path), "-H", '"Authorization: Bearer $ONVOYAGE_ADMIN_TOKEN"']
            if body is not None:
                parts += ["-H", "'Content-Type: application/json'", "-d", shlex.quote(json.dumps(body, ensure_ascii=False))]
            self.out(" ".join(parts))
        else:
            self.out(f"{method} {path}")
            if body is not None:
                self.out("    " + json.dumps(body, ensure_ascii=False))
        if method == "GET":
            return 200, ([] if path.split("?")[0].endswith("/creators") or path.split("?")[0].endswith("/places") else None)
        self.counter += 1
        route = path.split("?")[0]
        fake_id = f"<creator:{body['handle']}>" if method == "POST" and route.endswith("/creators") and isinstance(body, dict) else f"<id-{self.counter}>"
        return 200, {"id": fake_id, "poiId": fake_id}


# ---------------------------------------------------------------------------------------------------------------------- import
@dataclass
class Summary:
    done: list[str] = field(default_factory=list)
    skipped: list[str] = field(default_factory=list)
    problems: list[str] = field(default_factory=list)


def describe(result: Any) -> str:
    if isinstance(result, dict):
        return str(result.get("detail") or result.get("title") or result.get("type") or result)[:300]
    return str(result)[:300]


def profile_body(row: Row) -> dict[str, Any]:
    return {
        "handle": row.get("handle").lstrip("@"),
        "displayName": row.get("display_name"),
        "bio": row.get("bio") or None,
        "avatarPath": row.get("avatar_path") or None,
        "languages": [lang.lower() for lang in split_list(row.get("languages"))],
        "specialties": split_list(row.get("specialties")),
        "destinationIds": split_list(row.get("destination_ids")),
        "links": [{"kind": kind, "url": row.get(f"link_{kind}")} for kind in LINK_KINDS if row.get(f"link_{kind}")],
    }


def content_body(row: Row) -> dict[str, Any]:
    chapters, _ = parse_chapters(row.get("chapters"))
    published = parse_date(row.get("published_at")) if row.get("published_at") else None
    duration = parse_clock(row.get("duration_seconds")) if row.get("duration_seconds") else None
    return {
        "url": row.get("url"),
        "title": row.get("title"),
        "captionExcerpt": row.get("caption_excerpt") or None,
        "coverPath": row.get("cover_path") or None,
        "publishedAt": published.strftime("%Y-%m-%dT%H:%M:%SZ") if published else None,
        "durationSeconds": duration,
        "kind": row.get("kind").lower() or None,
        "isCommercial": bool(parse_bool(row.get("is_commercial"))),
        "chapters": [{"startSeconds": start, "title": title} for start, title in chapters],
    }


def resolve_place(transport: Transport, row: Row, destination: str, cache: dict[str, str]) -> tuple[str | None, str | None]:
    """The poiId of the row: given, or found by exact (accent- and case-insensitive) name. Never guesses among several."""
    if row.get("poi_id"):
        return row.get("poi_id"), None
    name = row.get("place")
    folded = fold(name)
    if getattr(transport, "offline", False):
        return f"<poiId:{name}>", None
    if folded in cache:
        return cache[folded], None
    query = f"{API}/places?query={quote(name)}&limit=20" + (f"&destination={quote(destination)}" if destination else "")
    status, found = transport.request("GET", query)
    if status != 200 or not isinstance(found, list):
        return None, f"recherche du lieu « {name} » refusée (HTTP {status}) : {describe(found)}"
    exact = [place for place in found if fold(str(place.get("name", ""))) == folded]
    if len(exact) == 1:
        cache[folded] = str(exact[0]["poiId"])
        return cache[folded], None
    if not found and status == 200:
        return None, f"lieu « {name} » introuvable dans le répertoire (destination {destination or 'toutes'}) : vérifiez l'orthographe ou donnez poi_id"
    names = ", ".join(f"« {p.get('name')} »" for p in found[:5])
    return None, (f"lieu « {name} » ambigu ({len(exact)} correspondances exactes) : donnez poi_id" if exact else f"lieu « {name} » sans correspondance exacte ; proches : {names}")


def import_dataset(data: Dataset, transport: Transport, destination: str = "marseille", update_profiles: bool = False, out: Callable[[str], None] = print) -> Summary:
    summary = Summary()
    cache: dict[str, str] = {}
    for creator in data.creators:
        key, handle = creator.get("key"), creator.get("handle").lstrip("@")
        out(f"# {handle}")
        status, listing = transport.request("GET", f"{API}/creators?search={quote(handle)}&limit=50")
        if status != 200 or not isinstance(listing, list):
            summary.problems.append(f"{handle}: liste des créateurs refusée (HTTP {status}) : {describe(listing)}")
            if status in (401, 403):
                summary.problems.append("jeton absent, expiré (60 min) ou sans rôle admin : relancez `login`")
                return summary
            continue
        existing = next((c for c in listing if str(c.get("handle", "")).lower() == handle.lower()), None)
        if existing:
            creator_id = str(existing["id"])
            if update_profiles:
                status, result = transport.request("PUT", f"{API}/creators/{creator_id}", profile_body(creator))
                (summary.done if status == 200 else summary.problems).append(f"{handle}: mise à jour du profil" if status == 200 else f"{handle}: profil refusé ({describe(result)})")
            else:
                summary.skipped.append(f"{handle}: profil déjà présent, non modifié (--update-profiles pour l'écraser)")
        else:
            status, result = transport.request("POST", f"{API}/creators", profile_body(creator))
            if status not in (200, 201) or not isinstance(result, dict):
                summary.problems.append(f"{handle}: création refusée (HTTP {status}) : {describe(result)}")
                continue
            creator_id = str(result["id"])
            summary.done.append(f"{handle}: profil créé")

        status, detail = transport.request("GET", f"{API}/creators/{creator_id}")
        detail = detail if isinstance(detail, dict) else {}

        ref = creator.get("consent_document_ref")
        if ref and detail.get("termsDocumentRef") != ref:
            accepted = parse_date(creator.get("consent_accepted_at"))
            status, result = transport.request("PUT", f"{API}/creators/{creator_id}/consent", {"documentRef": ref, "acceptedAt": accepted.strftime("%Y-%m-%dT%H:%M:%SZ") if accepted else None})
            (summary.done if status == 200 else summary.problems).append(f"{handle}: consentement fondateur enregistré" if status == 200 else f"{handle}: consentement refusé ({describe(result)})")
        elif ref:
            summary.skipped.append(f"{handle}: consentement déjà enregistré")

        if creator.get("account_id") and str(detail.get("accountId") or "").lower() != creator.get("account_id").lower():
            status, result = transport.request("PUT", f"{API}/creators/{creator_id}/account", {"accountId": creator.get("account_id")})
            (summary.done if status == 200 else summary.problems).append(f"{handle}: compte rattaché" if status == 200 else f"{handle}: compte refusé ({describe(result)})")

        known_contents = {str(c.get("permalink")): str(c["id"]) for c in detail.get("contents", []) or []}
        content_ids: dict[str, str] = dict(known_contents)
        for row in [r for r in data.contents if r.get("creator_key") == key]:
            parsed = parse_content_url(row.get("url"))
            if parsed is None:
                continue
            permalink = parsed[2]
            if permalink in known_contents:
                summary.skipped.append(f"{handle}: contenu déjà présent ({permalink})")
                continue
            status, result = transport.request("POST", f"{API}/creators/{creator_id}/contents", content_body(row))
            if status in (200, 201) and isinstance(result, dict):
                content_ids[permalink] = str(result["id"])
                summary.done.append(f"{handle}: contenu ajouté ({permalink})")
            else:
                summary.problems.append(f"{handle}: contenu {permalink} refusé (HTTP {status}) : {describe(result)}")

        known_links = {(str(link.get("poiId")), str(link.get("contentId") or ""), link.get("startSeconds")) for link in detail.get("placeLinks", []) or []}
        for row in [r for r in data.place_links if r.get("creator_key") == key]:
            poi, error = resolve_place(transport, row, destination, cache)
            if poi is None:
                summary.problems.append(f"{handle}: {error}")
                continue
            parsed = parse_content_url(row.get("content_url")) if row.get("content_url") else None
            content_id = content_ids.get(parsed[2]) if parsed else None
            if parsed and content_id is None:
                summary.problems.append(f"{handle}: association de « {row.get('place') or poi} » ignorée, contenu absent ({parsed[2]})")
                continue
            start = parse_clock(row.get("start_time")) if row.get("start_time") else None
            if (poi, content_id or "", start) in known_links:
                summary.skipped.append(f"{handle}: association déjà présente ({row.get('place') or poi})")
                continue
            body = {"poiId": poi, "contentId": content_id, "startSeconds": start, "status": (row.get("status") or "validated").lower()}
            status, result = transport.request("POST", f"{API}/creators/{creator_id}/place-links", body)
            (summary.done if status in (200, 201) else summary.problems).append(
                f"{handle}: association ajoutée ({row.get('place') or poi})" if status in (200, 201) else f"{handle}: association « {row.get('place') or poi} » refusée (HTTP {status}) : {describe(result)}")

        known_tips = {str(tip.get("poiId")): str(tip.get("text")) for tip in detail.get("tips", []) or []}
        for row in [r for r in data.tips if r.get("creator_key") == key]:
            poi, error = resolve_place(transport, row, destination, cache)
            if poi is None:
                summary.problems.append(f"{handle}: {error}")
                continue
            if known_tips.get(poi) == row.get("text"):
                summary.skipped.append(f"{handle}: conseil déjà présent ({row.get('place') or poi})")
                continue
            status, result = transport.request("PUT", f"{API}/creators/{creator_id}/tips/{poi}", {"text": row.get("text")})
            (summary.done if status == 200 else summary.problems).append(
                f"{handle}: conseil enregistré ({row.get('place') or poi})" if status == 200 else f"{handle}: conseil « {row.get('place') or poi} » refusé (HTTP {status}) : {describe(result)}")

        if parse_bool(creator.get("publish")):
            if detail.get("status") == "published":
                summary.skipped.append(f"{handle}: déjà publié")
            else:
                status, result = transport.request("POST", f"{API}/creators/{creator_id}/publish")
                (summary.done if status == 200 else summary.problems).append(f"{handle}: publié" if status == 200 else f"{handle}: publication refusée (HTTP {status}) : {describe(result)}")
        else:
            summary.skipped.append(f"{handle}: reste en brouillon (publish = non) : à publier après relecture dans /admin/creators")
    return summary


# ---------------------------------------------------------------------------------------------------------------------- CLI
def require_base_url(value: str | None) -> str:
    if not value:
        raise SystemExit("--base-url est obligatoire (ex. https://staging.on.voyage : le Gateway, pas le service).")
    parsed = urlparse(value)
    if parsed.scheme != "https" and parsed.hostname not in ("localhost", "127.0.0.1"):
        raise SystemExit("--base-url doit être en https:// (le jeton administrateur ne circule jamais en clair).")
    return value.rstrip("/")


def token_from_environment() -> str:
    token = os.environ.get("ONVOYAGE_ADMIN_TOKEN", "").strip()
    if not token:
        raise SystemExit("Jeton absent : exportez ONVOYAGE_ADMIN_TOKEN (voir `login`). Il n'est pas accepté sur la ligne de commande.")
    return token


def login(base_url: str, email: str) -> int:
    transport = HttpTransport(base_url, "")

    def post(path: str, body: dict[str, str]) -> tuple[int, Any]:
        request = urllib.request.Request(base_url + path, data=json.dumps(body).encode(), method="POST", headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=transport.timeout) as response:  # noqa: S310
                return response.status, _json(response.read())
        except urllib.error.HTTPError as error:
            return error.code, _json(error.read())

    status, result = post("/api/platform/v1/auth/otp/request", {"email": email})
    if status not in (200, 202):
        print(f"Demande de code refusée (HTTP {status}) : {describe(result)}", file=sys.stderr)
        return 1
    code = getpass.getpass("Code à 6 chiffres reçu par e-mail : ").strip()
    status, result = post("/api/platform/v1/auth/otp/verify", {"email": email, "code": code})
    if status != 200 or not isinstance(result, dict) or "accessToken" not in result:
        print(f"Code refusé (HTTP {status}) : {describe(result)}", file=sys.stderr)
        return 1
    if "admin" not in (result.get("roles") or []):
        print("Attention : ce compte n'a pas le rôle admin (STAGING_BOOTSTRAP_ADMIN_EMAIL).", file=sys.stderr)
    print("# Valable 60 minutes. Collez dans votre shell (ne l'enregistrez dans aucun fichier du dépôt) :", file=sys.stderr)
    print(f"export ONVOYAGE_ADMIN_TOKEN={result['accessToken']}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="import-founder-creators", description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("validate", "dry-run", "apply"):
        p = sub.add_parser(name)
        p.add_argument("folder", type=Path)
        if name != "validate":
            p.add_argument("--destination", default="marseille", help="slug de destination pour chercher les lieux (défaut marseille)")
            p.add_argument("--update-profiles", action="store_true", help="écrase le profil d'un créateur déjà présent")
            p.add_argument("--base-url", help="URL du Gateway")
        if name == "dry-run":
            p.add_argument("--format", choices=("http", "curl"), default="http")
            p.add_argument("--read-online", action="store_true", help="les lectures (créateurs, lieux) interrogent le vrai serveur ; les écritures restent affichées seulement")
    log = sub.add_parser("login")
    log.add_argument("--base-url", required=True)
    log.add_argument("--email", required=True)
    args = parser.parse_args(argv)

    if args.command == "login":
        return login(require_base_url(args.base_url), args.email)

    data, issues = load_dataset(args.folder)
    if not issues or not has_errors(issues):
        issues += validate(data)
    for issue in issues:
        print(issue, file=sys.stderr)
    errors = sum(1 for i in issues if i.severity == "error")
    print(f"{len(data.creators)} créateur(s), {len(data.contents)} contenu(s), {len(data.place_links)} association(s), {len(data.tips)} conseil(s) : {errors} erreur(s), {len(issues) - errors} avertissement(s).")
    if errors:
        return 1
    if args.command == "validate":
        return 0
    if args.command == "apply" and any(row.get("handle").lstrip("@").lower().startswith("demo_") for row in data.creators):
        print("Refus : le jeu contient des pseudonymes demo_* (jeu de démonstration). Ne l'importez jamais ailleurs qu'en `dry-run`.", file=sys.stderr)
        return 1

    if args.command == "dry-run":
        reader = HttpTransport(require_base_url(args.base_url), token_from_environment()) if args.read_online else None
        transport: Transport = DryRunTransport(print, args.format, reader, args.base_url or "$BASE_URL")
        print("--- DRY RUN : aucune écriture ---")
    else:
        transport = HttpTransport(require_base_url(args.base_url), token_from_environment())
    summary = import_dataset(data, transport, args.destination, args.update_profiles)
    print(f"\n{len(summary.done)} fait(s), {len(summary.skipped)} ignoré(s), {len(summary.problems)} problème(s).")
    for line in summary.problems:
        print(f"  PROBLÈME {line}", file=sys.stderr)
    if args.command == "dry-run":
        print("Rien n'a été écrit. Relancez avec `apply` pour exécuter ces appels.")
    return 1 if summary.problems else 0


if __name__ == "__main__":
    sys.exit(main())
