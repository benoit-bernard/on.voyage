#!/usr/bin/env python3
"""One-shot import of the former static blog (branch `main`, GitHub Pages) into content files of Web.Public (T-903, D-15).

For each article: title, description, kicker and meta line from the hero, and the body of <article> with Tailwind classes, share buttons,
icons and the hot-linked hero image removed (the site self-hosts everything; no third-party request). Relative links between articles are
rewritten to the new /micro-aventures/... paths. Run from the repository root: `python3 tools/import-legacy-blog.py`.
"""
import json, re, subprocess, sys
from html.parser import HTMLParser
from pathlib import Path

ARTICLES = [
    "blog/equipement-micro-aventure", "blog/silence-arctique",
    "eau/allier-bivouac", "eau/descente-ardeche", "eau/dordogne-famille", "eau/gorges-verdon",
]
OUT = Path("src/Web/OnVoyage.Web.Public/Content/micro-aventures")

def git_show(path):
    return subprocess.run(["git", "show", f"main:{path}"], check=True, capture_output=True, text=True).stdout

def first(pattern, text, flags=re.S):
    m = re.search(pattern, text, flags)
    return m.group(1) if m else ""

def clean(text):
    return re.sub(r"\s+", " ", re.sub(r"<[^>]+>", "", text)).strip()

def body_of(html):
    start = html.index("<article")
    start = html.index(">", start) + 1
    end = html.rindex("</article>")
    body = html[start:end]
    body = re.sub(r"<!--.*?-->", "", body, flags=re.S)
    # share buttons and the like (links to '#')
    body = re.sub(r'<a href="#"[^>]*>.*?</a>', "", body, flags=re.S)
    body = re.sub(r'<span class="material-icons-outlined[^"]*">[^<]*</span>', "", body)
    # hot-linked images are dropped (third-party requests)
    body = re.sub(r"<img[^>]*>", "", body, flags=re.S)
    body = re.sub(r'\sclass="[^"]*"', "", body)
    body = re.sub(r'\s(target|rel)="[^"]*"', "", body)
    return body

def rewrite_links(body, section):
    def repl(m):
        href = m.group(1)
        if href.startswith(("http", "#", "mailto:")):
            return m.group(0)
        target = href.replace(".html", "")
        target = re.sub(r"^\.\./", "", target)
        if target in ("index", "sur-leau", "sur-terre"):
            new = {"index": "/micro-aventures", "sur-leau": "/micro-aventures/eau", "sur-terre": "/micro-aventures/terre"}[target]
        elif "/" in target:
            new = "/micro-aventures/" + target
        else:
            new = f"/micro-aventures/{section}/{target}"
        return f'href="{new}"'
    return re.sub(r'href="([^"]*)"', repl, body)

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for path in ARTICLES:
        html = git_show(path + ".html")
        section = path.split("/")[0]
        hero = first(r'(<div class="article-hero.*?</div>\s*</div>)', html)
        kicker = clean(first(r'<span class="inline-block[^>]*>(.*?)</span>', hero))
        meta = clean(first(r'<p class="text-white/60[^>]*>(.*?)</p>', hero))
        data = {
            "path": path,
            "section": section,
            "title": clean(first(r"<h1[^>]*>(.*?)</h1>", hero)),
            "pageTitle": clean(first(r"<title>(.*?)</title>", html)),
            "description": first(r'<meta name="description" content="([^"]*)"', html),
            "kicker": kicker,
            "meta": meta,
            "body": rewrite_links(body_of(html), section).strip(),
        }
        target = OUT / (path.replace("/", "__") + ".json")
        target.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("imported", path, "->", target, f"({len(data['body'])} chars)")

if __name__ == "__main__":
    sys.exit(main())
